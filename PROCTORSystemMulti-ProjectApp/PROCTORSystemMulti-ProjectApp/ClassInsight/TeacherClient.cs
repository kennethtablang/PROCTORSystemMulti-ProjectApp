using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using ClassInsight.Shared.Discovery;
using Microsoft.AspNetCore.SignalR;        // HubException
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// The teacher app's live link to the classroom server: LAN discovery, the SignalR
/// connection, teacher registration (with the join code), and the roster/telemetry
/// event stream. UI-agnostic — it raises plain events the view model marshals to the
/// UI thread. WebRTC viewing is added in a later slice.
/// </summary>
public sealed class TeacherClient : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<RosterSnapshot>? RosterUpdated;
    public event Action<StudentSnapshot>? StudentJoined;
    public event Action<string>? StudentLeft;
    public event Action<string, StudentState>? StudentStateChanged;
    public event Action<string, TelemetrySnapshot>? TelemetryUpdated;
    public event Action<string>? Status;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>
    /// The live signalling connection, shared with a <see cref="ScreenStreamReceiver"/>
    /// so the focus view's WebRTC offer/answer/ICE travels over the same hub. Null until
    /// <see cref="ConnectAsync"/> succeeds.
    /// </summary>
    internal HubConnection? Hub => _hub;

    public async Task<ConnectOutcome> ConnectAsync(string joinCode, string deviceName, string platform, CancellationToken ct)
    {
        Status?.Invoke("Searching for classroom server…");

        var discovery = new LanDiscoveryClient("teacher");
        DiscoveryResponse? server = null;
        for (var i = 0; i < 5 && server is null && !ct.IsCancellationRequested; i++)
            server = await discovery.FindServerAsync(TimeSpan.FromSeconds(3), ct);

        if (server is null)
        {
            Status?.Invoke("No classroom server found on this network.");
            return ConnectOutcome.NoServer;
        }

        Status?.Invoke($"Connecting to \"{server.RoomName}\"…");

        _hub = new HubConnectionBuilder()
            .WithUrl(server.HubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hub.On<RosterSnapshot>(Protocol.ClientMethods.RosterUpdated, r => RosterUpdated?.Invoke(r));
        _hub.On<StudentSnapshot>(Protocol.ClientMethods.StudentJoined, s => StudentJoined?.Invoke(s));
        _hub.On<string>(Protocol.ClientMethods.StudentLeft, id => StudentLeft?.Invoke(id));
        _hub.On<string, StudentState>(Protocol.ClientMethods.StudentStateChanged,
            (id, st) => StudentStateChanged?.Invoke(id, st));
        _hub.On<string, TelemetrySnapshot>(Protocol.ClientMethods.TelemetryUpdated,
            (id, t) => TelemetryUpdated?.Invoke(id, t));

        var info = new TeacherInfo
        {
            DeviceName = deviceName,
            Platform = platform,
            AppVersion = "0.1.0",
            JoinCode = joinCode,
        };

        try
        {
            await _hub.StartAsync(ct);
            await _hub.InvokeAsync(Protocol.ServerMethods.RegisterTeacher, info, ct);
        }
        catch (HubException ex) when (ex.Message.Contains("Protocol version", StringComparison.OrdinalIgnoreCase))
        {
            // The server speaks a different protocol version (FR-DISC-06). This isn't an
            // auth problem — no join code will help — so surface it as an incompatibility.
            Status?.Invoke($"\"{server.RoomName}\" needs a matching app version. {ex.Message}");
            await DisposeHubAsync();
            return ConnectOutcome.Incompatible;
        }
        catch (HubException)
        {
            // The server fails closed: it rejects the teacher role unless a valid code is
            // presented. Reaching here means this room requires a join code (we either
            // sent none or the wrong one), so the app should prompt for it.
            Status?.Invoke(string.IsNullOrWhiteSpace(joinCode)
                ? $"\"{server.RoomName}\" needs a join code."
                : "That join code was not accepted.");
            await DisposeHubAsync();
            return ConnectOutcome.NeedsJoinCode;
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Connection failed: {ex.Message}");
            await DisposeHubAsync();
            return ConnectOutcome.Error;
        }

        Status?.Invoke($"Connected to \"{server.RoomName}\".");
        return ConnectOutcome.Connected;
    }

    /// <summary>Ask the server to begin streaming a student's screen (WebRTC next slice).</summary>
    public Task RequestStreamAsync(string studentConnectionId, StreamQuality quality)
    {
        if (_hub is not { State: HubConnectionState.Connected }) return Task.CompletedTask;
        return _hub.SendAsync(Protocol.ServerMethods.RequestStream,
            new StreamRequest { StudentConnectionId = studentConnectionId, Quality = quality });
    }

    public async Task DisconnectAsync()
    {
        await DisposeHubAsync();
        Status?.Invoke("Disconnected.");
    }

    private async Task DisposeHubAsync()
    {
        if (_hub is null) return;
        try { await _hub.DisposeAsync(); }
        catch { /* ignore */ }
        _hub = null;
    }

    public async ValueTask DisposeAsync() => await DisposeHubAsync();
}

/// <summary>Why a connect attempt ended — lets the UI stay code-free unless the server asks.</summary>
public enum ConnectOutcome
{
    /// <summary>Registered as a teacher; the roster is on its way.</summary>
    Connected,
    /// <summary>A server was found but it requires a join code we didn't satisfy.</summary>
    NeedsJoinCode,
    /// <summary>A server was found but it speaks an incompatible protocol version (FR-DISC-06).</summary>
    Incompatible,
    /// <summary>No classroom server answered discovery on this network.</summary>
    NoServer,
    /// <summary>A transport/other error prevented connecting.</summary>
    Error,
}
