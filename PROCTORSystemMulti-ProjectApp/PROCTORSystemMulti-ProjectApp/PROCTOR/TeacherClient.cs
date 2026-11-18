using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using PROCTOR.Shared.Discovery;
using Microsoft.AspNetCore.SignalR;        // HubException
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// The teacher app's live link to the classroom server: LAN discovery (several rooms may answer,
/// FR-DISC-04), a manual-address fallback for broadcast-blocked networks (NFR-COMPAT-02), the
/// SignalR connection, teacher registration (with the join code), and the roster / telemetry /
/// WebRTC-signalling event stream. UI-agnostic — it raises plain events the view model and the
/// <see cref="StreamManager"/> marshal as needed.
/// </summary>
public sealed class TeacherClient : IAsyncDisposable
{
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromMilliseconds(Protocol.BeaconIntervalMs + 600);

    private HubConnection? _hub;

    // One connect attempt at a time: the background reconnect loop and a teacher's manual
    // attempt (code / address / room) must not race over the hub.
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // The registration that last succeeded, replayed after a reconnect so the server restores
    // our roster entry (FR-DISC-05). Null until we've registered at least once.
    private TeacherInfo? _registration;

    // The server we first bound to. On an automatic reconnect we only re-attach to this same
    // endpoint (host+port+room), so a rogue server that appears on the LAN after the real one
    // drops can't harvest our join code. Matched on host/port/room (not ServerId, which is a
    // fresh GUID each server start) so a legitimate server restart still reconnects.
    private DiscoveryResponse? _pinnedServer;

    // The TLS key that pinned server proved (not merely advertised). When the server PC changes
    // address (DHCP renewal, another Wi-Fi, cable vs Wi-Fi) the same server is followed by this
    // key: a server at a new address is accepted only if its TLS handshake presents exactly this
    // key, which a rogue can't fake, so the join code is still never sent anywhere else.
    private string? _pinnedFingerprint;

    // Distinguishes a drop we caused (disconnect/dispose/failed attempt) from a real network or
    // server-restart drop, so only the latter triggers re-discovery.
    private volatile bool _intentionalClose;

    public event Action<RosterSnapshot>? RosterUpdated;
    public event Action<StudentSnapshot>? StudentJoined;
    public event Action<string>? StudentLeft;
    public event Action<string, StudentState>? StudentStateChanged;
    public event Action<string, TelemetrySnapshot>? TelemetryUpdated;
    public event Action<string>? Status;

    /// <summary>Classroom control (P5): lock state, raised hands and the room's limits.</summary>
    public event Action<string, bool>? StudentLockChanged;
    public event Action<string, bool>? StudentHandChanged;
    public event Action<ClassPolicy>? PolicyUpdated;
    public event Action<PollResults>? PollResultsUpdated;
    public event Action<LessonTimer>? TimerUpdated;
    public event Action<ChatMessage>? ChatReceived;
    public event Action<FileOffer>? FileOffered;
    public event Action<FileChunk>? FileChunkReceived;
    public event Action<FileFinish>? FileFinished;
    public event Action<string>? FileCancelled;
    public event Action<FileReceipt>? FileResultReceived;
    public event Action<string>? RemoteControlEnded;

    /// <summary>Screen broadcast signalling (FR-CTRL-02), routed to <see cref="ScreenBroadcaster"/>.</summary>
    public event Action<SdpMessage>? BroadcastAnswerReceived;
    public event Action<IceCandidateMessage>? BroadcastIceReceived;
    public event Action<string>? BroadcastStudentJoined;

    /// <summary>WebRTC signalling relayed from a student, routed by <see cref="StreamManager"/>.</summary>
    public event Action<SdpMessage>? OfferReceived;
    public event Action<IceCandidateMessage>? IceCandidateReceived;

    /// <summary>
    /// Raised after every successful registration — the first connect and each re-registration
    /// after a reconnect. The server has forgotten any streams by then, so viewers re-request.
    /// </summary>
    public event Action? Registered;

    /// <summary>Raised when the connection is lost for good (SignalR's auto-reconnect gave up, or
    /// a reconnected session could no longer re-register), so the app can re-discover from scratch.</summary>
    public event Action? ConnectionLost;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>The server currently connected to (or last attempted), for display and code retries.</summary>
    public DiscoveryResponse? CurrentServer { get; private set; }

    /// <summary>
    /// A server whose TLS key the teacher hasn't confirmed yet (outcome <see cref="ConnectOutcome.ConfirmServer"/>):
    /// the server, the key it presented, and — if this room/address was trusted before with another
    /// key — that previous trust (its identity changed: possibly an impostor).
    /// </summary>
    public (DiscoveryResponse Server, string Fingerprint, TrustedServer? Previous)? PendingTrust { get; private set; }

    /// <summary>The teacher confirmed the pending server's key: trust it from now on.</summary>
    public DiscoveryResponse? TrustPending()
    {
        if (PendingTrust is not { } p) return null;
        TrustedServers.Trust(p.Fingerprint, p.Server.RoomName, p.Server.Host);
        PendingTrust = null;
        return p.Server;
    }

    /// <summary>The fingerprint of the server we're connected to (for display).</summary>
    public string? CurrentFingerprint { get; private set; }

    /// <summary>When the last discovery heard several rooms and none was preferred, the choices (FR-DISC-04).</summary>
    public IReadOnlyList<DiscoveryResponse> DiscoveredRooms { get; private set; } = [];

    /// <summary>STUN/TURN servers the room is configured with (relay fallback); empty on a flat LAN.</summary>
    public IReadOnlyList<IceServerConfig> IceServers { get; private set; } = [];

    /// <summary>
    /// Find a server and connect to it as a teacher.
    /// </summary>
    /// <param name="target">Connect to exactly this server (a room the teacher picked, or a code retry).</param>
    /// <param name="preferredRoom">If several rooms answer, take this one automatically (the last room used).</param>
    /// <param name="manualAddress">host[:port] to try when broadcast discovery finds nothing.</param>
    public async Task<ConnectOutcome> ConnectAsync(
        string joinCode,
        string deviceName,
        string platform,
        CancellationToken ct,
        DiscoveryResponse? target = null,
        string? preferredRoom = null,
        string? manualAddress = null)
    {
        await _connectGate.WaitAsync(ct);
        try
        {
            // An untargeted attempt that queued behind one which already connected is moot.
            if (target is null && IsConnected) return ConnectOutcome.Connected;
            return await ConnectCoreAsync(joinCode, deviceName, platform, ct, target, preferredRoom, manualAddress);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<ConnectOutcome> ConnectCoreAsync(
        string joinCode,
        string deviceName,
        string platform,
        CancellationToken ct,
        DiscoveryResponse? target,
        string? preferredRoom,
        string? manualAddress)
    {
        _intentionalClose = false;
        DiscoveredRooms = [];

        // A join code is a credential for one specific room. An untargeted attempt with no pinned
        // server could land on whatever answers discovery (possibly a rogue), so it never carries
        // a code: codes go only to the server that asked (a targeted retry) or the pinned server.
        if (target is null && _pinnedServer is null) joinCode = string.Empty;

        var server = target ?? await FindServerAsync(preferredRoom, manualAddress, ct);
        if (server is null)
        {
            if (DiscoveredRooms.Count > 1)
            {
                Status?.Invoke($"{DiscoveredRooms.Count} classrooms found — choose one.");
                return ConnectOutcome.ChooseRoom;
            }
            Status?.Invoke(_pinnedServer is null
                ? "No classroom server found on this network."
                : $"Waiting for \"{_pinnedServer.RoomName}\" ({_pinnedServer.Host}) to come back…");
            return ConnectOutcome.NoServer;
        }

        CurrentServer = server;
        Status?.Invoke($"Connecting to \"{server.RoomName}\"…");

        // Authenticate the server before anything (a join code included) is sent (NFR-SEC-06):
        // its TLS key must be one this teacher has confirmed.
        var presented = await ServerTrust.ProbeAsync(server.Host, server.Port, TimeSpan.FromSeconds(5), ct);
        if (presented is null)
        {
            Status?.Invoke($"Couldn't open a secure connection to \"{server.RoomName}\" ({server.Host}).");
            return ConnectOutcome.Error;
        }
        if (!TrustedServers.IsTrusted(presented))
        {
            PendingTrust = (server, presented, TrustedServers.PreviousFor(server.RoomName, server.Host, presented));
            Status?.Invoke($"Confirm the identity of \"{server.RoomName}\" before connecting.");
            return ConnectOutcome.ConfirmServer;
        }
        // Followed to a new address by its advertised key: only the very same TLS key will do.
        if (target is null && _pinnedServer is { } pinned && !SameEndpoint(pinned, server)
            && !string.Equals(presented, _pinnedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Status?.Invoke($"Waiting for \"{pinned.RoomName}\" to come back…");
            return ConnectOutcome.NoServer;
        }
        PendingTrust = null;
        CurrentFingerprint = presented;

        // Make sure a previous connection is gone before building a new one.
        await DisposeHubAsync();
        _intentionalClose = false;

        var hub = new HubConnectionBuilder()
            .WithUrl(server.HubUrl, options =>
            {
                // Every request and the WebSocket (including automatic reconnects) accept only the confirmed key.
                options.HttpMessageHandlerFactory = inner =>
                {
                    if (inner is HttpClientHandler handler) handler.ServerCertificateCustomValidationCallback = ServerTrust.HttpValidator(presented);
                    return inner;
                };
                options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = ServerTrust.Validator(presented);
            })
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
        hub.On<string, bool>(Protocol.ClientMethods.StudentLockChanged, (id, locked) => StudentLockChanged?.Invoke(id, locked));
        hub.On<string, bool>(Protocol.ClientMethods.StudentHandChanged, (id, raised) => StudentHandChanged?.Invoke(id, raised));
        hub.On<ClassPolicy>(Protocol.ClientMethods.PolicyUpdated, p => PolicyUpdated?.Invoke(p));
        hub.On<PollResults>(Protocol.ClientMethods.PollResultsUpdated, r => PollResultsUpdated?.Invoke(r));
        hub.On<LessonTimer>(Protocol.ClientMethods.TimerUpdated, t => TimerUpdated?.Invoke(t));
        hub.On<ChatMessage>(Protocol.ClientMethods.ChatReceived, m => ChatReceived?.Invoke(m));
        hub.On<FileOffer>(Protocol.ClientMethods.FileOffered, o => FileOffered?.Invoke(o));
        hub.On<FileChunk>(Protocol.ClientMethods.FileChunkReceived, c => FileChunkReceived?.Invoke(c));
        hub.On<FileFinish>(Protocol.ClientMethods.FileFinished, f => FileFinished?.Invoke(f));
        hub.On<string>(Protocol.ClientMethods.FileCancelled, id => FileCancelled?.Invoke(id));
        hub.On<FileReceipt>(Protocol.ClientMethods.FileResultReceived, r => FileResultReceived?.Invoke(r));
        hub.On<string>(Protocol.ClientMethods.RemoteControlEnded, s => RemoteControlEnded?.Invoke(s));
        hub.On<SdpMessage>(Protocol.ClientMethods.ReceiveBroadcastAnswer, a => BroadcastAnswerReceived?.Invoke(a));
        hub.On<IceCandidateMessage>(Protocol.ClientMethods.ReceiveBroadcastIce, c => BroadcastIceReceived?.Invoke(c));
        hub.On<string>(Protocol.ClientMethods.BroadcastStudentJoined, id => BroadcastStudentJoined?.Invoke(id));
        hub.On<SdpMessage>(Protocol.ClientMethods.ReceiveOffer, o => OfferReceived?.Invoke(o));
        hub.On<IceCandidateMessage>(Protocol.ClientMethods.ReceiveIceCandidate, c => IceCandidateReceived?.Invoke(c));

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
                await LoadIceServersAsync(hub, CancellationToken.None);
                Status?.Invoke($"Reconnected to \"{server.RoomName}\".");
                Registered?.Invoke();
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
            AppVersion = global::PROCTOR.Shared.ProductVersion.Text,
            JoinCode = joinCode,
        };

        try
        {
            await hub.StartAsync(ct);
            await hub.InvokeAsync(Protocol.ServerMethods.RegisterTeacher, info, ct);
            _registration = info;    // remember what worked, to replay on reconnect
            _pinnedServer = server;  // and only reconnect to this same endpoint from now on…
            _pinnedFingerprint = presented;   // …or to this same key at a new address
            KnownServers.Remember(server.Host, server.Port, presented, server.RoomName);   // reachable later from other networks
            await LoadIceServersAsync(hub, ct);
        }
        catch (HubException ex) when (ex.Message.Contains("Protocol version", StringComparison.OrdinalIgnoreCase))
        {
            // The server speaks a different protocol version (FR-DISC-06). This isn't an
            // auth problem — no join code will help — so surface it as an incompatibility.
            Status?.Invoke($"\"{server.RoomName}\" needs a matching app version. {ex.Message}");
            await DisposeHubAsync();
            return ConnectOutcome.Incompatible;
        }
        catch (HubException ex) when (ex.Message.Contains("Too many attempts", StringComparison.OrdinalIgnoreCase))
        {
            // The SignalR client wraps the server's message; show just the server's part.
            var at = ex.Message.IndexOf("Too many attempts", StringComparison.OrdinalIgnoreCase);
            Status?.Invoke(ex.Message[at..]);
            await DisposeHubAsync();
            return ConnectOutcome.NeedsJoinCode;
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
        Registered?.Invoke();
        return ConnectOutcome.Connected;
    }

    /// <summary>
    /// Resolve which server to dial. Returns null with <see cref="DiscoveredRooms"/> set when the
    /// teacher must choose between several rooms.
    /// </summary>
    private async Task<DiscoveryResponse?> FindServerAsync(string? preferredRoom, string? manualAddress, CancellationToken ct)
    {
        Status?.Invoke("Searching for classroom servers…");
        var rooms = await new LanDiscoveryClient("teacher").FindServersAsync(DiscoveryWindow, ct);

        if (_pinnedServer is { } pin)
        {
            // Once bound, an automatic reconnect re-attaches to that same endpoint only — never
            // hand the (possibly set) join code to a different server that answered discovery,
            // which could be a rogue that popped up after the real one dropped.
            var same = rooms.FirstOrDefault(r => SameEndpoint(pin, r));
            if (same is not null) return same;

            // The server PC may have moved to a new address (DHCP, another Wi-Fi). Its advertised
            // key is only a hint here: ConnectCoreAsync checks the real TLS key before anything is sent.
            var moved = rooms.FirstOrDefault(r =>
                _pinnedFingerprint is not null
                && string.Equals(r.Fingerprint, _pinnedFingerprint, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.RoomName, pin.RoomName, StringComparison.Ordinal));
            if (moved is not null)
            {
                Status?.Invoke($"\"{pin.RoomName}\" moved to {moved.Host}; reconnecting…");
                return moved;
            }

            // Broadcast may be blocked (the pin may have come from a manual address): ask it directly.
            var direct = await ManualServer.ResolveAsync($"{pin.Host}:{pin.Port}", TimeSpan.FromSeconds(3), ct);
            return direct is not null && SameEndpoint(pin, direct) ? direct : null;
        }

        if (rooms.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(manualAddress))
            {
                Status?.Invoke($"No broadcast reply; trying {manualAddress}…");
                var typed = await ManualServer.ResolveAsync(manualAddress, TimeSpan.FromSeconds(4), ct);
                if (typed is not null) return typed;
            }
            return await FindKnownServerAsync(ct);
        }

        if (rooms.Count == 1) return rooms[0];

        var preferred = rooms.Where(r => LanDiscoveryClient.MatchesRoom(r, preferredRoom)).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredRoom) && preferred.Length == 1) return preferred[0];

        DiscoveredRooms = rooms;
        return null;
    }

    private async Task LoadIceServersAsync(HubConnection hub, CancellationToken ct)
    {
        try
        {
            IceServers = await hub.InvokeAsync<IReadOnlyList<IceServerConfig>>(Protocol.ServerMethods.GetIceServers, ct) ?? [];
        }
        catch
        {
            IceServers = [];   // older server / transient: direct LAN connectivity still works
        }
    }

    /// <summary>
    /// Send a classroom command (message, lock, open site, power…) to the given students, or to all
    /// with an empty list. Returns how many students it reached; throws with the server's reason
    /// (e.g. an invalid web address) if it was refused.
    /// </summary>
    public async Task<int> SendCommandAsync(ClassroomCommand command)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try
        {
            return await hub.InvokeAsync<int>(Protocol.ServerMethods.SendCommand, command);
        }
        catch (HubException ex)
        {
            throw new InvalidOperationException(ServerMessage(ex), ex);
        }
    }

    /// <summary>Set the room's web/app/internet/sound limits; returns the policy as the server cleaned it.</summary>
    public async Task<ClassPolicy> SetPolicyAsync(ClassPolicy policy)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try
        {
            return await hub.InvokeAsync<ClassPolicy>(Protocol.ServerMethods.SetPolicy, policy);
        }
        catch (HubException ex)
        {
            throw new InvalidOperationException(ServerMessage(ex), ex);
        }
    }

    /// <summary>Put a poll to the class; returns it with its id, or throws with the server's reason.</summary>
    public async Task<Poll> StartPollAsync(Poll poll)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { return await hub.InvokeAsync<Poll>(Protocol.ServerMethods.StartPoll, poll); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    /// <summary>Start showing this screen; returns the students to connect to.</summary>
    public async Task<IReadOnlyList<string>> StartBroadcastAsync(BroadcastRequest request)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { return await hub.InvokeAsync<IReadOnlyList<string>>(Protocol.ServerMethods.StartBroadcast, request); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    public async Task StopBroadcastAsync()
    {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return;
        await hub.InvokeAsync(Protocol.ServerMethods.StopBroadcast);
    }

    // ---- file transfer -------------------------------------------------------

    private async Task<T> Call<T>(string method, object? arg)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { return await hub.InvokeAsync<T>(method, arg); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    private async Task Call(string method, object? arg)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { await hub.InvokeAsync(method, arg); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    public Task StartRemoteControlAsync(string student) => Call(Protocol.ServerMethods.StartRemoteControl, student);
    public Task StopRemoteControlAsync(string student) => Call(Protocol.ServerMethods.StopRemoteControl, student);
    public Task SendRemoteInputAsync(RemoteInputBatch batch) => Call(Protocol.ServerMethods.SendRemoteInput, batch);

    public Task<FileOffer> OfferFileAsync(FileOffer offer) => Call<FileOffer>(Protocol.ServerMethods.OfferFile, offer);
    public Task SendFileChunkAsync(FileChunk chunk) => Call(Protocol.ServerMethods.SendFileChunk, chunk);
    public Task FinishFileAsync(FileFinish finish) => Call(Protocol.ServerMethods.FinishFile, finish);
    public Task CancelFileAsync(string transferId) => Call(Protocol.ServerMethods.CancelFile, transferId);
    public Task ReportFileResultAsync(FileReceipt result) => Call(Protocol.ServerMethods.ReportFileResult, result);
    public Task<int> RequestHandInAsync(HandInRequest request) => Call<int>(Protocol.ServerMethods.RequestHandIn, request);

    public async Task EndHandInAsync()
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        await hub.InvokeAsync(Protocol.ServerMethods.EndHandIn);
    }

    public async Task SendChatAsync(ChatSend send)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { await hub.InvokeAsync(Protocol.ServerMethods.SendChat, send); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    public async Task<int> WakeComputersAsync(WakeRequest request)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { return await hub.InvokeAsync<int>(Protocol.ServerMethods.WakeComputers, request); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    public async Task StartTimerAsync(TimerRequest request)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { await hub.InvokeAsync<LessonTimer>(Protocol.ServerMethods.StartTimer, request); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    public async Task StopTimerAsync()
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { await hub.InvokeAsync(Protocol.ServerMethods.StopTimer); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    public async Task ClosePollAsync()
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected.");
        try { await hub.InvokeAsync(Protocol.ServerMethods.ClosePoll); }
        catch (HubException ex) { throw new InvalidOperationException(ServerMessage(ex), ex); }
    }

    public Task LowerHandAsync(string studentConnectionId) => SendAsync(Protocol.ServerMethods.LowerHand, studentConnectionId);

    /// <summary>The server's own text from a wrapped <see cref="HubException"/>.</summary>
    private static string ServerMessage(HubException ex)
    {
        const string marker = "HubException: ";
        var at = ex.Message.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? ex.Message[(at + marker.Length)..] : ex.Message;
    }

    /// <summary>Fire-and-forget a hub call if connected (signalling and stream control).</summary>
    internal async Task SendAsync(string method, object arg)
    {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return;
        try { await hub.SendAsync(method, arg); }
        catch { /* link state surfaces via Reconnecting/Closed */ }
    }

    public async Task DisconnectAsync()
    {
        _pinnedServer = null;   // a user-initiated disconnect clears the pin; next connect is fresh
        _pinnedFingerprint = null;
        await DisposeHubAsync();
        Status?.Invoke("Disconnected.");
    }

    /// <summary>Forget the pinned server so the next connect discovers rooms afresh (the teacher chose "switch room").</summary>
    public async Task ForgetServerAsync()
    {
        _pinnedServer = null;
        _pinnedFingerprint = null;
        _registration = null;
        CurrentServer = null;
        await DisposeHubAsync();
    }

    // Same server endpoint across a restart: host + port + room (ServerId is a fresh GUID each
    // server start, so it can't be used to recognise a restarted server).
    /// <summary>
    /// No broadcast reply: dial the servers this device connected to before (see <see cref="KnownServers"/>),
    /// all at once, and take the most recent one that still presents the key it had.
    /// </summary>
    private async Task<DiscoveryResponse?> FindKnownServerAsync(CancellationToken ct)
    {
        var known = KnownServers.Recent();
        if (known.Count == 0) return null;
        Status?.Invoke(known.Count == 1
            ? $"No broadcast reply on this network; trying \"{known[0].RoomName}\" at {known[0].Host}…"
            : $"No broadcast reply on this network; trying {known.Count} classroom servers used before…");

        var attempts = known.Select(k => ManualServer.ResolveAsync($"{k.Host}:{k.Port}", TimeSpan.FromSeconds(3), ct)).ToArray();
        var results = await Task.WhenAll(attempts);
        for (var i = 0; i < known.Count; i++)
        {
            if (results[i] is { } r && string.Equals(r.Fingerprint, known[i].Fingerprint, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return null;
    }

    private static bool SameEndpoint(DiscoveryResponse a, DiscoveryResponse b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.RoomName, b.RoomName, StringComparison.Ordinal);

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
    /// <summary>Several rooms answered; the teacher must pick one (FR-DISC-04).</summary>
    ChooseRoom,
    /// <summary>
    /// The server presented a TLS key this teacher hasn't confirmed (first connection, or its identity
    /// changed). See <see cref="TeacherClient.PendingTrust"/>; nothing was sent to it.
    /// </summary>
    ConfirmServer,
    /// <summary>No classroom server answered discovery on this network.</summary>
    NoServer,
    /// <summary>A transport/other error prevented connecting.</summary>
    Error,
}
