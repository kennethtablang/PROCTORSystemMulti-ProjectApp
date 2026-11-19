using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using PROCTOR.Agent.Services;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using PROCTOR.Shared.Discovery;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent;

/// <summary>
/// The agent's lifecycle: discover the classroom server on the LAN with no configuration,
/// register as a student (no code, no login — FR-SEC-01), stream live telemetry on a fixed
/// cadence, and serve WebRTC screen streams on demand. It reconnects and re-registers on
/// its own if the server restarts or the link drops (FR-DISC-05).
///
/// Optional IT settings (<c>PROCTOR</c> section of appsettings.json — none are needed on a
/// normal lab LAN): <c>RoomName</c> pins the agent to one room when several share a network
/// (FR-SRV-05); <c>ServerAddress</c> pins the agent to one server by address — broadcast discovery
/// is then not used at all (networks that block broadcast, NFR-COMPAT-02, or to rule out a rogue
/// server on the LAN); <c>ReportBrowserActivity</c> toggles the browser-host
/// telemetry (FR-AGENT-06).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentWorker : BackgroundService
{
    /// <summary>The PROCTOR system version (Directory.Build.props), also what agent auto-update compares.</summary>
    public static readonly string AgentVersion = ProductVersion.Text;
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(1);   // ≥ 1 Hz (NFR-PERF-03)
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    private readonly ILogger<AgentWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // One screen sender per watching teacher, keyed by the teacher's connection id (FR-AGENT-08).
    private readonly ConcurrentDictionary<string, ScreenStreamSender> _senders = new();
    // Serialises start/stop per teacher so a quick quality switch can't race its own teardown.
    private readonly SemaphoreSlim _senderGate = new(1, 1);

    private readonly string? _roomName;
    private readonly string? _serverAddress;
    private readonly string _machineId;
    private readonly SystemTelemetryCollector _telemetry;
    private readonly BrowserActivityProbe? _browser;
    private readonly ClassroomUi? _ui;
    private readonly PolicyEnforcer _enforcer;
    private volatile HubConnection? _hub;
    private BroadcastReceiver? _broadcast;

    private IReadOnlyList<IceServerConfig> _iceServers = [];

    // IT ceiling for stream resolution (bandwidth / CPU budget per lab).
    private readonly int _maxStreamWidth;

    // Local IT switch: refuse log off / restart / shut down whatever the server says.
    private readonly bool _allowPower;

    // File transfer: files from the teacher and handing work in (null when turned off by IT).
    private readonly StudentFiles? _files;

    // Which server's TLS key this agent trusts, and this PC's own key (NFR-SEC-06).
    private readonly ServerPin _pin;
    private readonly AgentUpdater _updater;
    private bool _updateGateLogged;
    private readonly AgentIdentity _identity;

    // Remote control (FR-CTRL-05): null unless IT opted in (see the constructor).
    private readonly RemoteInput? _remote;
    private volatile string? _remoteTeacher;
    private DateTime _lastFolderOpened = DateTime.MinValue;   // "open on arrival": one Explorer window per batch

    public AgentWorker(ILogger<AgentWorker> logger, ILoggerFactory loggerFactory, IConfiguration configuration)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;

        var section = configuration.GetSection("PROCTOR");
        _roomName = NullIfBlank(section["RoomName"]);
        _maxStreamWidth = section.GetValue<int?>("MaxStreamWidth") ?? StreamResolution.DefaultMaxWidth;
        _allowPower = section.GetValue<bool?>("AllowPowerCommands") ?? true;
        ScreenCapture.DisableDxgi = string.Equals(section["CaptureBackend"], "GDI", StringComparison.OrdinalIgnoreCase);
        _serverAddress = NullIfBlank(section["ServerAddress"]);
        _pin = new ServerPin(section["ServerFingerprint"], logger);
        _updater = new AgentUpdater(logger);
        _identity = new AgentIdentity(section["EnrollmentKey"], logger);

        if (section.GetValue<bool?>("ReportBrowserActivity") ?? true)
            _browser = new BrowserActivityProbe(loggerFactory.CreateLogger<BrowserActivityProbe>());

        // Classroom control (P5): messages, lock screen, notices and the raise-hand tray icon, plus
        // enforcement of the room's web/app/internet/sound limits.
        if (section.GetValue<bool?>("EnableClassroomControl") ?? true)
        {
            _ui = new ClassroomUi(loggerFactory.CreateLogger<ClassroomUi>());
            _ui.HandToggled += raised => _ = SendHandAsync(raised);
            _ui.PollAnswered += answer => _ = SendPollAnswerAsync(answer);
            _ui.ChatSent += text => _ = SendChatAsync(text);

            // Remote control is opt-in, and only towards a server IT pinned by fingerprint: a
            // trusted-on-first-use pin could have been captured by a rogue server, which must never
            // be able to type and click on student desktops.
            if (section.GetValue<bool?>("AllowRemoteControl") ?? false)
            {
                if (_pin.IsStrict)
                    _remote = new RemoteInput(loggerFactory.CreateLogger<RemoteInput>());
                else
                    logger.LogWarning("AllowRemoteControl is on but ignored: remote control also needs PROCTOR:ServerFingerprint " +
                                      "(the fingerprint the classroom server prints at start-up).");
            }

            if (section.GetValue<bool?>("AllowFileTransfer") ?? true)
            {
                _files = new StudentFiles(loggerFactory.CreateLogger<StudentFiles>());
                _ui.HandInRefreshRequested += () => _ = Task.Run(RefreshHandIn);
                _ui.HandInFolderRequested += () => _ = Task.Run(() =>
                {
                    try { UserSession.OpenFolder(StudentFiles.HandInFolder(), _logger); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Couldn't open the Hand in folder."); _ui.SetHandInStatus("Couldn't open the folder."); }
                });
                _ui.HandInSubmitted += names => _ = HandInAsync(names);
            }
        }
        _enforcer = new PolicyEnforcer(loggerFactory.CreateLogger<PolicyEnforcer>(), _browser, _ui);

        _machineId = InstallId.GetOrCreate(logger);
        _telemetry = new SystemTelemetryCollector(_machineId, _browser);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var info = BuildStudentInfo();
        _logger.LogInformation("PROCTOR agent {Version} starting on {Machine} ({User}), {Displays} display(s){Room}.",
            AgentVersion, info.MachineName, info.UserName ?? "no user", info.Displays.Count,
            _roomName is null ? "" : $", room \"{_roomName}\"");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Session ended with an error; retrying.");
                }

                await TearDownSendersAsync();
                LiftClassroomState();

                if (!ct.IsCancellationRequested)
                    await SafeDelay(ReconnectDelay, ct);
            }
        }
        finally
        {
            await TearDownSendersAsync();
            _enforcer.Dispose();
            _ui?.Dispose();
            _browser?.Dispose();
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        var found = await FindTrustedServerAsync(ct);
        if (found is null) return;
        var (server, presented) = found.Value;
        _pin.Remember(presented);

        await using var hub = new HubConnectionBuilder()
            .WithUrl(server.HubUrl, options =>
            {
                // Every request and the WebSocket accept only the pinned key.
                options.HttpMessageHandlerFactory = inner =>
                {
                    if (inner is System.Net.Http.HttpClientHandler handler) handler.ServerCertificateCustomValidationCallback = ServerTrust.HttpValidator(presented);
                    return inner;
                };
                options.WebSocketConfiguration = ws => ws.RemoteCertificateValidationCallback = ServerTrust.Validator(presented);
            })
            .Build();

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        RegisterSignalingHandlers(hub);
        RegisterClassroomHandlers(hub);

        await hub.StartAsync(ct);
        _hub = hub;
        // Re-read identity each session: the signed-in user or display layout may have changed.
        // Prove which PC this is (NFR-SEC-06): sign the server's single-use challenge, bound to the TLS
        // key we pinned ourselves (not anything the server says about itself).
        var challenge = await hub.InvokeAsync<EnrollmentChallenge>(Protocol.ServerMethods.GetEnrollmentChallenge, ct);
        try
        {
            await hub.InvokeAsync(Protocol.ServerMethods.RegisterStudent, _identity.Sign(BuildStudentInfo(), presented, challenge.Nonce), ct);
        }
        catch (Microsoft.AspNetCore.SignalR.HubException ex) when (ex.Message.Contains("enrol", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("The classroom server refused this PC: {Reason} (agent key {Key}).", ex.Message, ServerTrust.Short(_identity.KeyId));
            throw;
        }
        _logger.LogInformation("Registered with the classroom server.");

        try
        {
            _iceServers = await hub.InvokeAsync<IReadOnlyList<IceServerConfig>>(Protocol.ServerMethods.GetIceServers, ct) ?? [];
        }
        catch (Exception ex)
        {
            _iceServers = [];
            _logger.LogDebug(ex, "Server offered no ICE servers.");
        }

        // Follow the teacher's PC: install the agent version it carries, if newer (in the background).
        // The update runs as SYSTEM, so, like remote control, only from a server IT pinned by
        // fingerprint: a trusted-on-first-use pin could belong to a rogue server that answered first.
        if (_pin.IsStrict && presented == _pin.Configured)
            _ = _updater.CheckAsync(hub, server, presented, ct);
        else if (!_updateGateLogged)
        {
            _updateGateLogged = true;
            _logger.LogInformation("Automatic agent updates are off: they need PROCTOR:ServerFingerprint " +
                                   "(build the setup with Build-AgentSetup.ps1 -ServerFingerprint).");
        }

        await TelemetryLoopAsync(hub, closed.Task, ct);
    }

    // Servers already reported as presenting the wrong key (logged once each, not every retry).
    private readonly HashSet<string> _refusedServers = new(StringComparer.Ordinal);

    private const int MaxProbedHosts = 64;

    private static readonly TimeSpan NotFoundFirstWarning = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NotFoundRepeatWarning = TimeSpan.FromMinutes(10);

    /// <summary>Say, in the Event Log, why this PC still has no server, and what to check.</summary>
    private void WarnStillSearching(TimeSpan elapsed, int probeTargets, int answered, int unreachable, int wrongKey)
    {
        var me = LanAddresses.Best();
        var tried = _serverAddress is not null
            ? $"the configured server address {_serverAddress}"
            : $"a discovery broadcast on UDP {Protocol.DiscoveryPort} to {probeTargets} address(es) ({string.Join(", ", LanAddresses.BroadcastAddresses())})";
        _logger.LogWarning(
            "No classroom server found for {Minutes:0} min. This PC: {Address}. Tried {Tried}: {Answered} server(s) answered, " +
            "{Unreachable} of them couldn't be reached over HTTPS, {WrongKey} presented a different key than this PC trusts. " +
            "Check that the server is running, that it is on the same network as this PC (a Wi-Fi access point in router mode " +
            "puts the server on a different network), and that the server PC's firewall allows TCP {Port} and UDP {DiscoveryPort}. " +
            "From this PC: Test-NetConnection <server address> -Port {Port}",
            elapsed.TotalMinutes, me?.Address.ToString() ?? "no network", tried, answered, unreachable, wrongKey,
            Protocol.DefaultServerPort, Protocol.DiscoveryPort, Protocol.DefaultServerPort);
    }

    /// <summary>
    /// Find a server and authenticate it (NFR-SEC-06): probe each candidate's TLS key and take the
    /// first that matches the pin (or, with no pin yet, the first reachable one — trust on first
    /// use). The fingerprint a server advertises in discovery is unauthenticated, so it only decides
    /// the order candidates are tried in; it can never rule one out, and a rogue reply can't stop
    /// the agent reaching the real server.
    /// </summary>
    private async Task<(DiscoveryResponse Server, string Fingerprint)?> FindTrustedServerAsync(CancellationToken ct)
    {
        var discovery = new LanDiscoveryClient("student");
        var searching = System.Diagnostics.Stopwatch.StartNew();
        var nextWarning = NotFoundFirstWarning;
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<DiscoveryResponse> candidates;
            if (_serverAddress is not null)
            {
                // IT pinned the address: use only it and never follow broadcast replies.
                var pinned = await ManualServer.ResolveAsync(_serverAddress, TimeSpan.FromSeconds(3), ct);
                candidates = pinned is not null && LanDiscoveryClient.MatchesRoom(pinned, _roomName) ? [pinned] : [];
            }
            else
            {
                // Every reply's Host is the datagram's real source address (DiscoveryCodec); the port and
                // everything else in it are the sender's claim. So candidates are grouped by address only
                // (at most two per machine — two rooms on one PC — whatever ports it advertises), and one
                // noisy host can't crowd others out. The address that worked last time (kept in HKLM, so
                // also after a restart) goes first; the advertised fingerprint only orders the rest.
                var pin = _pin.Current;
                var last = _pin.LastServerEndpoint;
                // Only the last good address (bound to where a reply really came from) is preferred —
                // never the advertised fingerprint, which any sender can claim.
                var heard = await discovery.FindServersAsync(TimeSpan.FromSeconds(3), ct,
                    preferred: r => $"{r.Host}:{r.Port}" == last);

                // The address that worked last time is always probed, even if discovery didn't hear it
                // (a flood of fake replies can delay finding the server, but never prevent it).
                if (last is not null && !heard.Any(r => $"{r.Host}:{r.Port}" == last)
                    && await ManualServer.ResolveAsync(last, TimeSpan.FromSeconds(3), ct) is { } direct)
                    heard = [direct, .. heard];

                candidates = heard
                    .Where(r => LanDiscoveryClient.MatchesRoom(r, _roomName))
                    .GroupBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g => g
                        .OrderBy(r => $"{r.Host}:{r.Port}" == last ? 0 : 1)
                        .ThenBy(r => pin is not null && r.Fingerprint == pin ? 0 : 1)
                        .ThenBy(r => r.Port == Protocol.DefaultServerPort ? 0 : 1)
                        .DistinctBy(r => r.Port)
                        .Take(LanDiscoveryClient.MaxRepliesPerHost))
                    .OrderBy(r => $"{r.Host}:{r.Port}" == last ? 0 : 1)
                    .ThenBy(r => pin is not null && r.Fingerprint == pin ? 0 : 1)
                    .Take(MaxProbedHosts)
                    .ToList();
            }

            // Probe the candidates in parallel (bounded), then take the first, in order, that passes.
            using var gate = new SemaphoreSlim(16);
            var probes = candidates.Select(async c =>
            {
                await gate.WaitAsync(ct);
                try { return await ServerTrust.ProbeAsync(c.Host, c.Port, TimeSpan.FromSeconds(5), ct); }
                finally { gate.Release(); }
            }).ToArray();
            var results = await Task.WhenAll(probes);

            for (var i = 0; i < candidates.Count; i++)
            {
                var server = candidates[i];
                var presented = results[i];
                if (presented is null)
                {
                    _logger.LogDebug("Couldn't open a secure connection to {Host}:{Port}.", server.Host, server.Port);
                    continue;
                }
                if (_pin.Current is { } pin && pin != presented)
                {
                    if (_refusedServers.Add(server.Host + "|" + presented))
                        _logger.LogError("Refusing \"{Room}\" at {Host}: it presents fingerprint {Presented}, but this PC trusts {Pinned}. " +
                                         "If the classroom server was reinstalled, set PROCTOR:ServerFingerprint (or, as an administrator, delete " +
                                         "TrustedServerFingerprint under HKLM\\SOFTWARE\\PROCTOR\\Agent).",
                            server.RoomName, server.Host, ServerTrust.Format(presented), ServerTrust.Format(pin));
                    continue;
                }
                if (warned)
                    _logger.LogWarning("Found \"{Room}\" at {Url} after {Minutes:0} min of searching.", server.RoomName, server.HubUrl, searching.Elapsed.TotalMinutes);
                else
                    _logger.LogInformation("Found \"{Room}\" at {Url}.", server.RoomName, server.HubUrl);
                _pin.LastServerEndpoint = $"{server.Host}:{server.Port}";
                return (server, presented);
            }

            if (candidates.Count == 0) _logger.LogDebug("No server yet; still searching…");
            if (searching.Elapsed >= nextWarning)
            {
                // The Event Log only records warnings and above: without this a PC that can't reach
                // the server looks perfectly healthy to the lab admin.
                nextWarning = searching.Elapsed + NotFoundRepeatWarning;
                warned = true;
                WarnStillSearching(searching.Elapsed, discovery.LastProbeTargets, candidates.Count,
                    unreachable: results.Count(r => r is null),
                    wrongKey: results.Count(r => r is not null && _pin.Current is { } p && p != r));
            }
            await SafeDelay(TimeSpan.FromSeconds(3), ct);
        }
        return null;
    }

    private void RegisterSignalingHandlers(HubConnection hub)
    {
        // A teacher asked to watch this student. The server tags the request with the
        // requesting teacher's connection id so we know whom to offer to.
        hub.On<string, StreamRequest>(Protocol.ClientMethods.StreamRequested, async (teacherId, request) =>
        {
            _logger.LogInformation("Teacher {Teacher} requested {Quality} stream of display {Display}.",
                teacherId, request.Quality, request.DisplayIndex);
            await StartSenderAsync(hub, teacherId, request);
        });

        hub.On<string>(Protocol.ClientMethods.StreamStopRequested, async teacherId =>
        {
            await _senderGate.WaitAsync();
            try
            {
                if (_senders.TryRemove(teacherId, out var sender))
                {
                    await sender.DisposeAsync();
                    _logger.LogInformation("Stopped stream to teacher {Teacher}; {Count} viewer(s) left.", teacherId, _senders.Count);
                }
            }
            finally
            {
                _senderGate.Release();
            }
        });

        hub.On<SdpMessage>(Protocol.ClientMethods.ReceiveAnswer, answer =>
        {
            if (_senders.TryGetValue(answer.FromConnectionId, out var sender))
                sender.HandleAnswer(answer);
        });

        hub.On<IceCandidateMessage>(Protocol.ClientMethods.ReceiveIceCandidate, candidate =>
        {
            if (_senders.TryGetValue(candidate.FromConnectionId, out var sender))
                sender.HandleIceCandidate(candidate);
        });
    }

    private void RegisterClassroomHandlers(HubConnection hub)
    {
        hub.On<string, ClassroomCommand>(Protocol.ClientMethods.CommandReceived, (_, command) => HandleCommand(command));
        hub.On<ClassPolicy>(Protocol.ClientMethods.PolicyUpdated, policy => _enforcer.Apply(policy));
        hub.On(Protocol.ClientMethods.HandLowered, () => _ui?.ResetHand(showNotice: true));
        hub.On<Poll>(Protocol.ClientMethods.PollStarted, poll => _ui?.ShowPoll(poll));
        hub.On<string>(Protocol.ClientMethods.PollClosed, id => _ui?.ClosePoll(id));

        hub.On<LessonTimer>(Protocol.ClientMethods.TimerUpdated, t => _ui?.ShowTimer(t));

        // Teacher's screen (FR-CTRL-02).
        hub.On<BroadcastInfo>(Protocol.ClientMethods.BroadcastStarted, async info =>
        {
            if (_ui is null) return;
            await StopBroadcastViewerAsync();
            _broadcast = new BroadcastReceiver(hub, info.TeacherConnectionId, _ui, _iceServers, _loggerFactory.CreateLogger<BroadcastReceiver>());
            _ui.ShowBroadcast(info.Mode, ClassroomLimits.CleanText(info.ShowingStudentName));
        });
        hub.On<SdpMessage>(Protocol.ClientMethods.ReceiveBroadcastOffer, async offer =>
        {
            if (_broadcast is { } b) await b.HandleOfferAsync(offer);
        });
        hub.On<IceCandidateMessage>(Protocol.ClientMethods.ReceiveBroadcastIce, c => _broadcast?.HandleIce(c));
        hub.On<bool>(Protocol.ClientMethods.ScreenShownToClass, shown => _ui?.ShowScreenShown(shown));
        hub.On<ChatMessage>(Protocol.ClientMethods.ChatReceived, m => { if (m.FromTeacher) _ui?.ShowChat(m); });

        // Files from the teacher, and collecting work.
        hub.On<FileOffer>(Protocol.ClientMethods.FileOffered, async offer =>
        {
            if (offer.HandIn) return;
            var error = _files is null ? "File transfer is turned off on this PC." : _files.Offered(offer);
            if (error is not null) await ReportFileAsync(hub, offer.TransferId, error);
        });
        hub.On<FileChunk>(Protocol.ClientMethods.FileChunkReceived, async chunk =>
        {
            if (_files is not null && !_files.Chunk(chunk)) await ReportFileAsync(hub, chunk.TransferId, "The file arrived out of order.");
        });
        hub.On<FileFinish>(Protocol.ClientMethods.FileFinished, async finish =>
        {
            if (_files is null) return;
            var (path, name, error, open) = await Task.Run(() => _files.Finished(finish));
            if (path is null && error is null) return;   // not ours
            if (error is null)
            {
                // The teacher chose "open on their PCs": show the file in Explorer. Several files
                // sent together open one window, not one each.
                var shown = false;
                if (open && path is not null && DateTime.UtcNow - _lastFolderOpened > TimeSpan.FromSeconds(10))
                {
                    try
                    {
                        UserSession.ShowFileInFolder(path, _logger);
                        _lastFolderOpened = DateTime.UtcNow;
                        shown = true;
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Couldn't open the folder of a file from the teacher."); }
                }
                _ui?.ShowNotice(shown || (open && DateTime.UtcNow - _lastFolderOpened < TimeSpan.FromSeconds(10))
                    ? $"Your teacher sent you \u201C{name}\u201D. It's open in \u201C{StudentFiles.ReceivedFolderName}\u201D."
                    : $"Your teacher sent you \u201C{name}\u201D. It's in \u201C{StudentFiles.ReceivedFolderName}\u201D on your desktop.", TimeSpan.FromSeconds(8));
            }
            await ReportFileAsync(hub, finish.TransferId, error);
        });
        hub.On<string>(Protocol.ClientMethods.FileCancelled, id => _files?.Cancelled(id));
        // Remote control: the server only relays input from the teacher holding control; the agent
        // also ignores input unless control was announced, and lets go of everything when it ends.
        hub.On<RemoteControlInfo>(Protocol.ClientMethods.RemoteControlChanged, info =>
        {
            if (_remote is null) return;
            if (info.Active)
            {
                // Another teacher taking over starts clean: let go of anything the previous one held.
                if (_remoteTeacher is not null && _remoteTeacher != info.TeacherConnectionId) _remote.ReleaseAll();
                _remoteTeacher = info.TeacherConnectionId;
                _ui?.ShowRemoteControl(true);
            }
            else if (_remoteTeacher == info.TeacherConnectionId)
            {
                _remoteTeacher = null;
                _remote.ReleaseAll();
                _ui?.ShowRemoteControl(false);
            }
        });
        hub.On<RemoteInputBatch>(Protocol.ClientMethods.RemoteInputReceived, batch =>
        {
            if (_remote is null || _remoteTeacher is null) return;
            try { _remote.Apply(batch); }
            catch (Exception ex) { _logger.LogDebug(ex, "Remote input failed."); }
        });

        hub.On<HandInInfo>(Protocol.ClientMethods.HandInRequested, info => { if (_files is not null) _ui?.ShowHandIn(info); });
        hub.On<FileReceipt>(Protocol.ClientMethods.FileResultReceived, r =>
            _ui?.SetHandInStatus(r.Ok ? "\u2714 Your teacher received your work." : "Your teacher couldn't save a file: " + (r.Error ?? "unknown error") + ". Try again."));
        hub.On<string>(Protocol.ClientMethods.BroadcastStopped, async teacher =>
        {
            if (_broadcast is { } b && b.Teacher == teacher) await StopBroadcastViewerAsync();
        });
    }

    /// <summary>Carry out a teacher's command (already validated by the server; checked again here).</summary>
    private void HandleCommand(ClassroomCommand command)
    {
        var text = ClassroomLimits.CleanText(command.Text);
        _logger.LogInformation("Teacher command: {Kind}.", command.Kind);
        if (command.Kind is CommandKind.LogOff or CommandKind.Restart or CommandKind.ShutDown && !_allowPower)
        {
            _logger.LogWarning("Ignored {Kind}: power commands are disabled on this PC (AllowPowerCommands=false).", command.Kind);
            return;
        }
        switch (command.Kind)
        {
            case CommandKind.Message when text is not null:
                _ui?.ShowMessage(text);
                break;
            case CommandKind.Lock:
                _ui?.Lock(ClassroomLimits.CleanLockTitle(command.Title), text, ClassroomLimits.CleanColor(command.Color));
                break;
            case CommandKind.Unlock:
                _ui?.Unlock();
                break;
            case CommandKind.LaunchProgram when ClassroomLimits.CleanProgram(command.Text) is { } program:
                if (!UserSession.LaunchProgram(program, _logger))
                    _logger.LogWarning("Couldn't start {Program} for the student.", program);
                break;
            case CommandKind.CloseAllPrograms:
                _ui?.ShowNotice("Your teacher is closing your programs. Save your work if asked.", TimeSpan.FromSeconds(6));
                ProgramCloser.CloseAll(_logger);
                break;
            case CommandKind.OpenUrl when ClassroomLimits.TryCleanUrl(command.Url, out var url):
                UserSession.OpenUrl(url, _logger);
                break;
            case CommandKind.LogOff:
                _ui?.ShowNotice("Your teacher is signing you out…", TimeSpan.FromSeconds(4));
                _ = Task.Delay(3000).ContinueWith(_ => UserSession.LogOff(_logger));
                break;
            case CommandKind.Restart:
                _ui?.ShowNotice($"Your teacher is restarting this computer in {ClassroomLimits.PowerCountdownSeconds} seconds. Save your work.", TimeSpan.FromSeconds(15));
                UserSession.Restart(_logger);
                break;
            case CommandKind.ShutDown:
                _ui?.ShowNotice($"Your teacher is shutting down this computer in {ClassroomLimits.PowerCountdownSeconds} seconds. Save your work.", TimeSpan.FromSeconds(15));
                UserSession.ShutDown(_logger);
                break;
        }
    }

    private async Task StopBroadcastViewerAsync()
    {
        var b = _broadcast;
        _broadcast = null;
        if (b is not null) await b.DisposeAsync();
        _ui?.HideBroadcast();
    }

    private async Task SendPollAnswerAsync(PollAnswer answer)
    {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return;
        try { await hub.SendAsync(Protocol.ServerMethods.SubmitPollAnswer, answer); }
        catch (Exception ex) { _logger.LogDebug(ex, "Poll answer failed."); }
    }

    private static async Task ReportFileAsync(HubConnection hub, string transferId, string? error)
    {
        try { await hub.SendAsync(Protocol.ServerMethods.ReportFileResult, new FileReceipt { TransferId = transferId, Ok = error is null, Error = error }); }
        catch { }
    }

    private void RefreshHandIn()
    {
        try { _ui?.SetHandInFiles(StudentFiles.HandInFiles()); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read the Hand in folder.");
            _ui?.SetHandInStatus("Couldn't read your Hand in folder.");
        }
    }

    /// <summary>Send the chosen files from the student's Hand in folder to the collecting teacher.</summary>
    private async Task HandInAsync(IReadOnlyList<string> names)
    {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) { _ui?.SetHandInStatus("Not connected to your teacher."); return; }
        var done = 0;
        foreach (var name in names)
        {
            try
            {
                _ui?.SetHandInStatus($"Sending \u201C{name}\u201D\u2026");
                await using var stream = await Task.Run(() => StudentFiles.OpenHandInFile(name));
                await FileSender.SendAsync(
                    o => hub.InvokeAsync<FileOffer>(Protocol.ServerMethods.OfferFile, o),
                    c => hub.InvokeAsync(Protocol.ServerMethods.SendFileChunk, c),
                    f => hub.InvokeAsync(Protocol.ServerMethods.FinishFile, f),
                    id => hub.InvokeAsync(Protocol.ServerMethods.CancelFile, id),
                    stream,
                    new FileOffer { FileName = name, Size = stream.Length, HandIn = true });
                done++;
            }
            catch (Microsoft.AspNetCore.SignalR.HubException ex)
            {
                var reason = ex.Message.Contains("HubException: ") ? ex.Message[(ex.Message.IndexOf("HubException: ", StringComparison.Ordinal) + 14)..] : ex.Message;
                _ui?.SetHandInStatus($"\u201C{name}\u201D wasn't handed in: {reason}");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hand-in of {Name} failed.", name);
                _ui?.SetHandInStatus($"\u201C{name}\u201D couldn't be sent.");
                return;
            }
        }
        _ui?.SetHandInStatus($"Sent {done} file(s). Waiting for your teacher to confirm\u2026");
    }

    private async Task SendChatAsync(string text)
    {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected })
        {
            _ui?.ChatFailed("not connected to the teacher.");
            return;
        }
        try { await hub.InvokeAsync(Protocol.ServerMethods.SendChat, new ChatSend { Text = text }); }
        catch (Microsoft.AspNetCore.SignalR.HubException ex) { _ui?.ChatFailed(ex.Message.Contains("too quickly") ? "you're sending messages too quickly." : "the teacher couldn't be reached."); }
        catch (Exception ex) { _logger.LogDebug(ex, "Chat send failed."); _ui?.ChatFailed("the teacher couldn't be reached."); }
    }

    private async Task SendHandAsync(bool raised)
    {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return;
        try { await hub.SendAsync(Protocol.ServerMethods.RaiseHand, raised); }
        catch (Exception ex) { _logger.LogDebug(ex, "Raise hand failed."); }
    }

    /// <summary>
    /// Fail-safe when the link to the server is lost: unlock, lift every limit and reset the raised
    /// hand, so a student is never left locked or restricted with no teacher able to undo it.
    /// </summary>
    private void LiftClassroomState()
    {
        _hub = null;
        _ui?.Unlock();
        _ui?.ResetHand(showNotice: false);
        _ui?.ResetPoll();
        _ui?.ChatDisconnected();
        _ui?.ResetHandIn();
        _remoteTeacher = null;
        _remote?.ReleaseAll();
        _ui?.ShowRemoteControl(false);
        _files?.Clear();
        _ui?.ShowTimer(new LessonTimer());
        _ = StopBroadcastViewerAsync();
        // Lost the server: programs, internet and sound limits are lifted (never strand a PC), but
        // the website whitelist / blacklist stays until the server says otherwise (a restart of the
        // agent clears it until it reconnects and gets it again).
        _enforcer.Apply(new ClassPolicy().WithWeb(_enforcer.Current.Web));
    }

    private async Task StartSenderAsync(HubConnection hub, string teacherId, StreamRequest request)
    {
        var displays = DisplayEnumerator.GetDisplays();
        var display = displays.ElementAtOrDefault(request.DisplayIndex);
        if (display.Width == 0) display = displays[0];   // out of range → primary

        await _senderGate.WaitAsync();
        try
        {
            // Replace any existing stream to this teacher (quality or display change / re-request).
            if (_senders.TryRemove(teacherId, out var existing))
                await existing.DisposeAsync();

            // Requested resolution, clamped to the IT ceiling, and never wider than the display itself.
            var width = Math.Min(StreamResolution.Effective(request.Quality, request.MaxWidth, _maxStreamWidth), display.Width);
            var sender = new ScreenStreamSender(hub, teacherId, request.Quality, display, _iceServers,
                _loggerFactory.CreateLogger<ScreenStreamSender>(), width);
            _senders[teacherId] = sender;
            try
            {
                await sender.StartAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start stream to {Teacher}.", teacherId);
                if (_senders.TryRemove(teacherId, out var s)) await s.DisposeAsync();
            }
        }
        finally
        {
            _senderGate.Release();
        }
    }

    private async Task TelemetryLoopAsync(HubConnection hub, Task closed, CancellationToken ct)
    {
        // Prime deltas so the first published sample is meaningful.
        _telemetry.Collect();

        using var timer = new PeriodicTimer(TelemetryInterval);
        while (!ct.IsCancellationRequested)
        {
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            var done = await Task.WhenAny(tick, closed);
            if (done == closed) return;                 // connection dropped; caller rediscovers
            if (!await tick) return;

            if (hub.State != HubConnectionState.Connected) continue;

            try
            {
                var sample = _telemetry.Collect();
                await hub.SendAsync(Protocol.ServerMethods.PushTelemetry, sample, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry push failed.");
            }
        }
    }

    private async Task TearDownSendersAsync()
    {
        await _senderGate.WaitAsync();
        try
        {
            foreach (var key in _senders.Keys.ToArray())
                if (_senders.TryRemove(key, out var sender))
                    await sender.DisposeAsync();
        }
        finally
        {
            _senderGate.Release();
        }
    }


    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Physical adapters' MACs for Wake-on-LAN, wired first (Wi-Fi rarely wakes from off). Virtual,
    /// loopback and tunnel adapters are skipped.
    /// </summary>
    private static string[] MacAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                                                    or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
                                                    or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                .Where(n => !IsVirtual(n.Description))
                .OrderBy(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? 1 : 0)
                .ThenBy(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up ? 0 : 1)
                .Select(n => WakeOnLan.Normalize(n.GetPhysicalAddress().ToString()))
                .OfType<string>()
                .Distinct()
                .Take(WakeOnLan.MaxPerMachine)
                .ToArray();
        }
        catch { return []; }

        static bool IsVirtual(string d) =>
            d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || d.Contains("VMware", StringComparison.OrdinalIgnoreCase) || d.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || d.Contains("TAP", StringComparison.Ordinal) || d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || d.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase);
    }

    private StudentInfo BuildStudentInfo()
    {
        var displays = DisplayEnumerator.GetDisplays();
        var primary = displays[0];

        return new StudentInfo
        {
            MachineId = _machineId,
            MachineName = Environment.MachineName,
            UserName = SignedInUser(),
            OsVersion = Environment.OSVersion.VersionString,
            IpAddress = string.Empty,     // the server fills this from the real socket address
            ScreenWidth = primary.Width,
            ScreenHeight = primary.Height,
            Displays = displays.Select(d => d.Info).ToArray(),
            MacAddresses = MacAddresses(),
            RemoteControlAllowed = _remote is not null,
            AgentVersion = AgentVersion,
        };
    }

    /// <summary>
    /// The student signed in to this session. Under the service supervisor the agent runs as
    /// SYSTEM inside the student's session, so the process user ("SYSTEM") is wrong — ask the
    /// session instead, falling back to the process user in console/dev mode.
    /// </summary>
    private static string? SignedInUser()
    {
        try
        {
            if (NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var session)
                && NativeMethods.GetSessionUserName(session) is { } user)
                return user;
        }
        catch { /* fall through */ }

        try
        {
            var name = Environment.UserName;
            return string.Equals(name, "SYSTEM", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// The agent's <see cref="StudentInfo.MachineId"/>: a random 128-bit identifier generated once per
/// install and persisted, so a student keeps its tile across reconnects (FR-SEC-07). It is not
/// derived from the machine name, MAC or any hardware attribute, so it can't be predicted or
/// forged by someone who merely knows the PC. Stored under %ProgramData%\PROCTOR (shared by
/// the service and console runs); falls back to the user profile if that isn't writable.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallId
{
    private const string FileName = "agent-id";

    public static string GetOrCreate(ILogger logger)
    {
        foreach (var root in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(root), "PROCTOR");
                var path = Path.Combine(dir, FileName);

                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (existing.Length == 32 && existing.All(Uri.IsHexDigit)) return existing;
                }

                Directory.CreateDirectory(dir);
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                File.WriteAllText(path, id);
                return id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not persist install id under {Root}.", root);
            }
        }

        // Nowhere writable: still random, just not stable across restarts.
        logger.LogWarning("Could not persist the agent install id; this machine will get a new tile after a restart.");
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }
}
