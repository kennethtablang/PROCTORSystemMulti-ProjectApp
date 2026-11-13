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

    // The registration that last succeeded, replayed after a reconnect so the server restores
    // our roster entry (FR-DISC-05). Null until we've registered at least once.
    private TeacherInfo? _registration;

    // Distinguishes a drop we caused (disconnect/dispose/failed attempt) from a real network or
    // server-restart drop, so only the latter triggers re-discovery.
    private volatile bool _intentionalClose;

    public event Action<RosterSnapshot>? RosterUpdated;
    public event Action<StudentSnapshot>? StudentJoined;
    public event Action<string>? StudentLeft;
    public event Action<string, StudentState>? StudentStateChanged;
    public event Action<string, TelemetrySnapshot>? TelemetryUpdated;
    public event Action<string>? Status;

    /// <summary>Raised when the connection is lost for good (SignalR's auto-reconnect gave up, or
    /// a reconnected session could no longer re-register), so the app can re-discover from scratch.</summary>
    public event Action? ConnectionLost;

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
        _intentionalClose = false;

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

        // Make sure a previous connection is gone before building a new one.
        await DisposeHubAsync();
        _intentionalClose = false;

        var hub = new HubConnectionBuilder()
            .WithUrl(server.HubUrl)
            .WithAutomaticReconnect()
            .Build();
        _hub = hub;

        hub.On<RosterSnapshot>(Protocol.ClientMethods.RosterUpdated, r => RosterUpdated?.Invoke(r));
        hub.On<StudentSnapshot>(Protocol.ClientMethods.StudentJoined, s => StudentJoined?.Invoke(s));
        hub.On<string>(Protocol.ClientMethods.StudentLeft, id => StudentLeft?.Invoke(id));
        hub.On<string, StudentState>(Protocol.ClientMethods.StudentStateChanged,
            (id, st) => StudentStateChanged?.Invoke(id, st));
        hub.On<string, TelemetrySnapshot>(Protocol.ClientMethods.TelemetryUpdated,
            (id, t) => TelemetryUpdated?.Invoke(id, t));

        // Reconnection handling (FR-DISC-05). SignalR's automatic reconnect restores the transport
        // after a transient drop, but the server — especially one that restarted — no longer knows
        // us, so we re-register to restore our roster entry and teacher role. If that fails, or if
        // SignalR gives up entirely, fall back to full re-discovery.
        hub.Reconnecting += _ =>
        {
            Status?.Invoke("Connection lost — reconnecting…");
            return Task.CompletedTask;
        };
        hub.Reconnected += async _ =>
        {
            try
            {
                if (_registration is not null)
                    await hub.InvokeAsync(Protocol.ServerMethods.RegisterTeacher, _registration);
                Status?.Invoke($"Reconnected to \"{server.RoomName}\".");
            }
            catch
            {
                _intentionalClose = true;
                ConnectionLost?.Invoke();
            }
        };
        hub.Closed += _ =>
        {
            if (!_intentionalClose)
            {
                Status?.Invoke("Disconnected from server. Re-discovering…");
                ConnectionLost?.Invoke();
            }
            return Task.CompletedTask;
        };

        var info = new TeacherInfo
        {
            DeviceName = deviceName,
            Platform = platform,
            AppVersion = "0.1.0",
            JoinCode = joinCode,
        };

        try
        {
            await hub.StartAsync(ct);
            await hub.InvokeAsync(Protocol.ServerMethods.RegisterTeacher, info, ct);
            _registration = info;   // remember what worked, to replay on reconnect
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
        _intentionalClose = true;   // a teardown we initiated must not kick off re-discovery
        var hub = _hub;
        _hub = null;
        try { await hub.DisposeAsync(); }
        catch { /* ignore */ }
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
