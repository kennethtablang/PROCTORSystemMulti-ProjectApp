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

    public async Task<bool> ConnectAsync(string joinCode, string deviceName, string platform, CancellationToken ct)
    {
        Status?.Invoke("Searching for classroom server…");

        var discovery = new LanDiscoveryClient("teacher");
        DiscoveryResponse? server = null;
        for (var i = 0; i < 5 && server is null && !ct.IsCancellationRequested; i++)
            server = await discovery.FindServerAsync(TimeSpan.FromSeconds(3), ct);

        if (server is null)
        {
            Status?.Invoke("No classroom server found on this network.");
            return false;
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
        catch (HubException ex)
        {
            // Most likely a bad/absent join code (server fails closed).
            Status?.Invoke($"Rejected: {ex.Message}");
            await DisposeHubAsync();
            return false;
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Connection failed: {ex.Message}");
            await DisposeHubAsync();
            return false;
        }

        Status?.Invoke($"Connected to \"{server.RoomName}\".");
        return true;
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
