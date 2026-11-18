using System.Net.NetworkInformation;
using System.Windows.Input;
using PROCTOR.Shared;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// "Connect student PCs" guide on the dashboard's first screen: the <b>real</b> address student PCs
/// must reach (the connected server's, else this PC's current LAN address, refreshed whenever the
/// network changes), the exact setup / test commands for it, and the network caveat for Wi-Fi.
/// </summary>
public sealed partial class DashboardViewModel
{
    private LanAddresses.Candidate? _thisPc;

    /// <summary>
    /// This PC runs the classroom service (installed by PROCTOR Teacher with "Run the classroom
    /// service on this PC": App\ and Server\ side by side). False on a teacher laptop installed as
    /// "teacher app only": it connects to the lab's teacher PC, so it must not tell anyone to point
    /// student PCs at itself.
    /// </summary>
    private static readonly bool HasLocalService = DetectLocalService();

    private static bool DetectLocalService()
    {
        try
        {
            return OperatingSystem.IsWindows()
                && File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Server", "PROCTOR.Server.exe"));
        }
        catch
        {
            return false;
        }
    }

    public ICommand CopyServerAddressCommand { get; private set; } = null!;
    public ICommand CopySetupCommandCommand { get; private set; } = null!;
    public ICommand CopyTestCommandCommand { get; private set; } = null!;
    public ICommand CopyServerKeyCommand { get; private set; } = null!;

    private void InitConnectGuide()
    {
        CopyServerAddressCommand = new Command(async () => await CopyAsync(StudentServerAddress, "Server address"));
        CopySetupCommandCommand = new Command(async () => await CopyAsync(SetupCommand, "Setup command"));
        CopyTestCommandCommand = new Command(async () => await CopyAsync(TestCommand, "Test command"));
        CopyServerKeyCommand = new Command(async () => await CopyAsync(ServerKey, "Server key"));

        RefreshThisPc();
        // Wi-Fi switched networks, cable plugged in, VPN up/down: the address to give students changes.
        try { NetworkChange.NetworkAddressChanged += (_, _) => OnUi(RefreshThisPc); } catch { }
    }

    private void RefreshThisPc()
    {
        _thisPc = LanAddresses.Best();
        RaiseConnectGuide();
    }

    private void RaiseConnectGuide()
    {
        Raise(nameof(ShowConnectGuide));
        Raise(nameof(ConnectGuideTitle));
        Raise(nameof(StudentServerAddress));
        Raise(nameof(AddressSourceText));
        Raise(nameof(SetupCommand));
        Raise(nameof(ServerKey));
        Raise(nameof(HasServerKey));
        Raise(nameof(ServerKeyHint));
        Raise(nameof(ShowStudentSteps));
        Raise(nameof(TestCommand));
        Raise(nameof(NetworkNote));
        Raise(nameof(HasNetworkNote));
        Raise(nameof(ManualAddressHint));
    }

    /// <summary>Shown until the first student PC appears.</summary>
    public bool ShowConnectGuide => !HasStudents;

    public string ConnectGuideTitle => ConnectedServer is { } s
        ? $"Connect student PCs to “{s.RoomName}”"
        : HasLocalService ? "Connect student PCs" : "Connect to the lab's teacher PC";

    /// <summary>Student-PC setup steps: only when they would point at a real server.</summary>
    public bool ShowStudentSteps => ConnectedServer is not null || HasLocalService;

    private global::PROCTOR.Shared.Contracts.DiscoveryResponse? ConnectedServer =>
        IsConnected ? _client.CurrentServer : null;

    private string ThisPcAddress => _thisPc is { } p ? $"{p.Address}:{Protocol.DefaultServerPort}" : "";

    /// <summary>host:port student PCs must reach. Never an example: the live server's, or this PC's.</summary>
    public string StudentServerAddress => ConnectedServer is { } s
        ? $"{s.Host}:{s.Port}"
        : _thisPc is null ? "No network connection"
        : HasLocalService ? ThisPcAddress : "Not connected yet";

    public string AddressSourceText => ConnectedServer is not null
        ? "Address of the classroom server this app is connected to."
        : !HasLocalService && _thisPc is not null
            ? "This PC runs the teacher app only. Type the lab teacher PC's address in the box below " +
              "(e.g. 192.168.2.10:5080; PROCTOR Teacher on that PC shows it) and press Connect. This app " +
              "remembers it. The student PCs stay set up as they are."
        : _thisPc is { } p
            ? $"Not connected to a server yet. When the PROCTOR Classroom Service runs on this PC, this is its address ({p.AdapterName})."
            : "This PC has no network connection. Connect it to the lab network first.";

    /// <summary>
    /// The connected server's key (fingerprint). Student PCs are locked to one key: a setup built for
    /// another teacher PC refuses this one (by design: nobody can pose as the teacher's PC), and the
    /// only symptom is a port test that passes while no student appears. So show it, and put it in
    /// the setup command.
    /// </summary>
    public string ServerKey => IsConnected && _client.CurrentFingerprint is { Length: > 0 } f ? ServerTrust.Format(f) : "";

    public bool HasServerKey => ServerKey.Length > 0;

    public string ServerKeyHint => HasServerKey
        ? "Student PCs must be set up with this key (the command below does it). A student setup made for another teacher PC refuses this one."
        : "Shown once this app is connected to the server.";

    public string SetupCommand => HasServerKey
        ? $"PROCTOR-Agent-Setup.exe SERVERFINGERPRINT=\"{ServerKey}\" SERVERADDRESS={StudentServerAddress}"
        : $"PROCTOR-Agent-Setup.exe SERVERADDRESS={StudentServerAddress}";

    public string TestCommand
    {
        get
        {
            var address = StudentServerAddress;
            var colon = address.LastIndexOf(':');
            return colon > 0
                ? $"Test-NetConnection {address[..colon]} -Port {address[(colon + 1)..]}"
                : $"Test-NetConnection {address} -Port {Protocol.DefaultServerPort}";
        }
    }

    /// <summary>
    /// The Wi-Fi caveat: a teacher laptop on an access point that runs as a router gets its own
    /// private network that wired lab PCs can't reach, so nothing ever connects.
    /// </summary>
    public string NetworkNote => _thisPc is { IsWireless: true } p && ServerIsThisPc
        ? $"This PC is on Wi-Fi ({p.AdapterName}). Student PCs on the switches can reach it only if the Wi-Fi " +
          "access point is in Access Point / bridge mode, so this PC gets an address on the same network as the " +
          "student PCs. If the access point is a router with its own network, the test command above fails: switch " +
          "it to Access Point mode, join a Wi-Fi that is on the lab network, or plug this PC into a switch."
        : "";

    public bool HasNetworkNote => NetworkNote.Length > 0;

    // Students need to reach this PC only when the server runs here (or nothing is connected yet).
    // A teacher laptop connected to a server on a wired PC can sit behind any Wi-Fi.
    private bool ServerIsThisPc => ConnectedServer is not { } s
        ? HasLocalService
        : (_thisPc is { } p && string.Equals(s.Host, p.Address.ToString(), StringComparison.OrdinalIgnoreCase))
        || s.Host is "127.0.0.1" or "localhost";

    public string ManualAddressHint => _thisPc is null
        ? "Server not found automatically? Enter its address (host:port)"
        : HasLocalService
            ? $"Server not found automatically? Enter its address (this PC: {ThisPcAddress})"
            : "Lab teacher PC's address, e.g. 192.168.2.10:5080";

    private async Task CopyAsync(string text, string what)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(text);
            Status = $"{what} copied: {text}";
        }
        catch
        {
            Status = $"Couldn't copy. {what}: {text}";
        }
    }
}
