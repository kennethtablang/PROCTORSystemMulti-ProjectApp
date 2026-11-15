using ClassInsight.Server.Services;
using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ClassInsight.Server.Hubs;

/// <summary>
/// The single coordination hub for the classroom. It does three jobs:
///   1. Roster: students register and are projected to teachers in real time.
///   2. Signalling relay: it shuttles opaque WebRTC SDP/ICE between an <b>authorised</b>
///      teacher↔student pair — and nobody else — without ever parsing the media (FR-SEC-04).
///   3. Telemetry fan-out: student samples are pushed to every connected teacher.
///
/// Students join with no code by design (FR-SEC-01, lab deployment). Teachers are gated:
/// open mode admits them freely (FR-SEC-03); otherwise a correct join code is required and
/// bad attempts are throttled (FR-SEC-02/06). A connection holds exactly one role.
/// </summary>
public sealed class ClassroomHub : Hub
{
    private readonly RoomRegistry _room;
    private readonly ServerIdentity _identity;
    private readonly TeacherAuthThrottle _throttle;
    private readonly PollBox _polls;
    private readonly ILogger<ClassroomHub> _logger;

    public ClassroomHub(
        RoomRegistry room,
        ServerIdentity identity,
        TeacherAuthThrottle throttle,
        PollBox polls,
        ILogger<ClassroomHub> logger)
    {
        _polls = polls;
        _room = room;
        _identity = identity;
        _throttle = throttle;
        _logger = logger;
    }

    // ---- registration -----------------------------------------------------

    /// <summary>A student agent announces itself. No code, no login (FR-SEC-01).</summary>
    public async Task RegisterStudent(StudentInfo info)
    {
        RejectIfIncompatible(info.ProtocolVersion);
        if (_room.IsTeacher(Context.ConnectionId))
            throw new HubException("This connection is already registered as a teacher.");

        // Prefer the real socket address the server sees over whatever the agent guessed.
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(ip))
            info = info with { IpAddress = ip };

        // Bound what an agent can make every teacher render.
        info = info with { Displays = info.Displays.Take(16).ToArray() };

        var entry = _room.AddOrUpdateStudent(Context.ConnectionId, info);
        _logger.LogInformation("Student joined: {Name} ({Ip})", info.MachineName, info.IpAddress);

        await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentJoined, entry.ToSnapshot());
        await BroadcastRosterToTeachers();

        // A student who joins mid-lesson gets the room's current web/app limits straight away.
        if (!_room.Policy.IsEmpty)
            await Clients.Caller.SendAsync(Protocol.ClientMethods.PolicyUpdated, _room.Policy);

        // ...and the open poll, if there is one.
        if (_polls.Open is { } poll)
        {
            await Clients.Caller.SendAsync(Protocol.ClientMethods.PollStarted, poll);
            await PushPollResultsAsync();
        }
    }

    /// <summary>
    /// A teacher device registers. Fails closed: unless the server runs open, a valid join
    /// code must be presented or the call throws <see cref="HubException"/> (which the
    /// teacher app reads as "needs code").
    /// </summary>
    public async Task RegisterTeacher(TeacherInfo info)
    {
        RejectIfIncompatible(info.ProtocolVersion);
        if (_room.IsStudent(Context.ConnectionId))
            throw new HubException("This connection is already registered as a student.");

        if (!_identity.AllowOpenTeacherMode)
        {
            var key = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? Context.ConnectionId;

            if (_throttle.GetLockout(key) is { } wait)
                throw new HubException($"Too many attempts. Try again in {Math.Ceiling(wait.TotalSeconds)}s.");

            if (!_identity.VerifyTeacherCode(info.JoinCode))
            {
                _throttle.RegisterFailure(key);
                _logger.LogWarning("Teacher registration rejected (bad code) from {Key}", key);
                throw new HubException("Invalid join code.");
            }

            _throttle.RegisterSuccess(key);
        }

        // Never keep the code around in the roster.
        _room.AddOrUpdateTeacher(Context.ConnectionId, info with { JoinCode = null });
        _logger.LogInformation("Teacher joined: {Device} ({Platform})", info.DeviceName, info.Platform);

        // Give the newly-admitted teacher the full roster and the room's current limits immediately.
        await Clients.Caller.SendAsync(Protocol.ClientMethods.RosterUpdated, _room.BuildRoster());
        await Clients.Caller.SendAsync(Protocol.ClientMethods.PolicyUpdated, _room.Policy);
        if (_polls.Results(_room.StudentCount) is { } results)
            await Clients.Caller.SendAsync(Protocol.ClientMethods.PollResultsUpdated, results);
    }

    // ---- polls ------------------------------------------------------------

    /// <summary>Put a poll to the whole class (replacing any open one). Returns it with its id.</summary>
    public async Task<Poll> StartPoll(Poll poll)
    {
        if (!_room.IsTeacher(Context.ConnectionId)) throw new HubException("Only teachers can start a poll.");
        var clean = PollLimits.Clean(poll, out var error) ?? throw new HubException(error ?? "Invalid poll.");

        if (_polls.Close() is { } previous)
            await Clients.Clients(_room.StudentConnectionIds).SendAsync(Protocol.ClientMethods.PollClosed, previous);

        var started = _polls.Start(clean);
        await Clients.Clients(_room.StudentConnectionIds).SendAsync(Protocol.ClientMethods.PollStarted, started);
        await PushPollResultsAsync();
        _logger.LogInformation("Teacher {Teacher} started a {Kind} poll for {Count} student(s).",
            Context.ConnectionId, started.Kind, _room.StudentCount);
        return started;
    }

    /// <summary>Close the open poll: students' answer cards disappear; results stay with the teachers.</summary>
    public async Task ClosePoll()
    {
        if (!_room.IsTeacher(Context.ConnectionId)) throw new HubException("Only teachers can close a poll.");
        await ClosePollAsync();
    }

    /// <summary>A student answers (or changes their answer to) the open poll.</summary>
    public async Task SubmitPollAnswer(PollAnswer answer)
    {
        var id = Context.ConnectionId;
        if (!_room.TryGetStudent(id, out var student)) return;
        var name = string.IsNullOrWhiteSpace(student.Info.UserName)
            ? student.Info.MachineName
            : $"{student.Info.MachineName} · {student.Info.UserName}";
        if (_polls.Submit(id, name, answer))
            await PushPollResultsAsync();
    }

    private async Task ClosePollAsync()
    {
        if (_polls.Close() is not { } closed) return;
        await Clients.Clients(_room.StudentConnectionIds).SendAsync(Protocol.ClientMethods.PollClosed, closed);
        await PushPollResultsAsync();
    }

    private async Task PushPollResultsAsync()
    {
        if (_polls.Results(_room.StudentCount) is { } results)
            await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.PollResultsUpdated, results);
    }

    // ---- classroom control (P5) -------------------------------------------

    /// <summary>
    /// A teacher's command to one, several or all students: message, lock/unlock, open a web page,
    /// log off / restart / shut down. Validated here (the agent validates again) and relayed only
    /// to registered students. Returns how many students it was sent to.
    /// </summary>
    public async Task<int> SendCommand(ClassroomCommand command)
    {
        var teacher = Context.ConnectionId;
        if (!_room.IsTeacher(teacher)) throw new HubException("Only teachers can send classroom commands.");
        if (command is null || !Enum.IsDefined(command.Kind)) throw new HubException("Unknown command.");

        // Power commands are the most disruptive thing a teacher can send. In open mode anyone on the
        // network can be a teacher, so they (like the other strong controls) are refused unless IT
        // has explicitly opted in.
        if (command.Kind is CommandKind.LogOff or CommandKind.Restart or CommandKind.ShutDown && StrongControlsBlocked)
            throw new HubException("Log off / restart / shut down need a teacher join code on this server (it runs in open mode).");

        var text = ClassroomLimits.CleanText(command.Text);
        string? url = null;
        switch (command.Kind)
        {
            case CommandKind.Message when text is null:
                throw new HubException("A message needs some text.");
            case CommandKind.OpenUrl:
                if (!ClassroomLimits.TryCleanUrl(command.Url, out var clean))
                    throw new HubException("Only http:// or https:// web addresses can be opened.");
                url = clean;
                break;
        }

        var targets = ResolveTargets(command.Targets);
        var relayed = new ClassroomCommand { Kind = command.Kind, Text = text, Url = url };

        foreach (var student in targets)
        {
            if (command.Kind == CommandKind.Lock && _room.LockStudent(student, teacher))
                await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentLockChanged, student, true);
            else if (command.Kind == CommandKind.Unlock && _room.UnlockStudent(student))
                await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentLockChanged, student, false);
        }

        if (targets.Count > 0)
            await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.CommandReceived, teacher, relayed);

        _logger.LogInformation("Teacher {Teacher} sent {Kind} to {Count} student(s).", teacher, command.Kind, targets.Count);
        return targets.Count;
    }

    /// <summary>Set the room-wide web/app/internet/sound limits. An empty policy clears them.</summary>
    public async Task<ClassPolicy> SetPolicy(ClassPolicy policy)
    {
        if (!_room.IsTeacher(Context.ConnectionId)) throw new HubException("Only teachers can set limits.");
        var clean = ClassroomLimits.Clean(policy);
        if (StrongControlsBlocked && (clean.BlockInternet || clean.BlockedApps.Count > 0))
            throw new HubException("Turning off the internet and blocking programs need a teacher join code on this server (it runs in open mode).");
        _room.Policy = clean;
        await PushPolicyAsync();
        _logger.LogInformation("Teacher {Teacher} set limits: web {Mode} ({Hosts} sites), {Apps} blocked apps, internet {Net}, mute {Mute}.",
            Context.ConnectionId, clean.WebMode, clean.WebHosts.Count, clean.BlockedApps.Count,
            clean.BlockInternet ? "off" : "on", clean.MuteSound);
        return clean;
    }

    /// <summary>A student raises (or lowers) their hand to ask for help.</summary>
    public async Task RaiseHand(bool raised)
    {
        var student = Context.ConnectionId;
        if (!_room.IsStudent(student)) return;
        if (_room.SetHand(student, raised))
            await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentHandChanged, student, raised);
    }

    /// <summary>A teacher acknowledges a raised hand; the student's request is cleared on their screen too.</summary>
    public async Task LowerHand(string studentConnectionId)
    {
        if (!_room.IsTeacher(Context.ConnectionId) || string.IsNullOrEmpty(studentConnectionId)) return;
        if (!_room.SetHand(studentConnectionId, false)) return;
        await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentHandChanged, studentConnectionId, false);
        await Clients.Client(studentConnectionId).SendAsync(Protocol.ClientMethods.HandLowered);
    }

    /// <summary>
    /// In open teacher mode anyone on the network can act as a teacher, so the strongest controls
    /// (log off / restart / shut down, internet off, closing programs) are refused unless IT opted
    /// in with <c>ClassInsight:AllowStrongControlsInOpenMode</c>. With a join code they're always available.
    /// </summary>
    private bool StrongControlsBlocked => _identity.AllowOpenTeacherMode && !_identity.AllowStrongControlsInOpenMode;

    /// <summary>Requested targets that are real students; an empty request means every student.</summary>
    private IReadOnlyList<string> ResolveTargets(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0) return _room.StudentConnectionIds.ToArray();
        return requested.Where(t => !string.IsNullOrEmpty(t) && _room.IsStudent(t))
                        .Distinct(StringComparer.Ordinal)
                        .Take(ClassroomLimits.MaxTargets)
                        .ToArray();
    }

    private async Task PushPolicyAsync()
    {
        await Clients.Clients(_room.StudentConnectionIds).SendAsync(Protocol.ClientMethods.PolicyUpdated, _room.Policy);
        await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.PolicyUpdated, _room.Policy);
    }

    /// <summary>Explicit roster pull (teachers normally rely on the pushed snapshot).</summary>
    public RosterSnapshot GetRoster()
    {
        if (!_room.IsTeacher(Context.ConnectionId))
            throw new HubException("Only teachers can read the roster.");
        return _room.BuildRoster();
    }

    /// <summary>
    /// STUN/TURN servers for WebRTC (relay fallback, P4). Only registered clients get them,
    /// because TURN entries carry credentials.
    /// </summary>
    public IReadOnlyList<IceServerConfig> GetIceServers()
    {
        var id = Context.ConnectionId;
        return _room.IsTeacher(id) || _room.IsStudent(id) ? _identity.IceServers : [];
    }

    // ---- streaming control (teacher -> student) ---------------------------

    /// <summary>
    /// A teacher asks to view a student's screen. This is the only way a viewing pair is
    /// authorised (FR-STREAM-05). We relay the request to the student tagged with the teacher's
    /// connection id; the agent then produces the WebRTC offer. A repeat request (e.g. a quality
    /// or display change) just re-sends — the agent replaces its stream to that teacher.
    /// </summary>
    public async Task RequestStream(StreamRequest request)
    {
        var teacher = Context.ConnectionId;
        if (string.IsNullOrEmpty(request?.StudentConnectionId)) return;
        if (!_room.AuthorizePair(teacher, request.StudentConnectionId)) return;   // not a teacher, or no such student

        await Clients.Client(request.StudentConnectionId)
            .SendAsync(Protocol.ClientMethods.StreamRequested, teacher, request);

        await SetStateAsync(request.StudentConnectionId, StudentState.Streaming);
    }

    /// <summary>
    /// A teacher stops viewing a student; the agent tears down that peer. The student returns to
    /// <see cref="StudentState.Online"/> only once no teacher is still watching (FR-STREAM-06).
    /// </summary>
    public async Task StopStream(string studentConnectionId)
    {
        var teacher = Context.ConnectionId;
        if (string.IsNullOrEmpty(studentConnectionId)) return;
        if (!_room.RevokePair(teacher, studentConnectionId)) return;

        await Clients.Client(studentConnectionId)
            .SendAsync(Protocol.ClientMethods.StreamStopRequested, teacher);

        if (_room.ViewerCount(studentConnectionId) == 0)
            await SetStateAsync(studentConnectionId, StudentState.Online);
    }

    // ---- signalling relay (opaque SDP/ICE, pair-gated) --------------------

    /// <summary>Student (sender) → teacher: the SDP offer. Only to a teacher authorised to view this student.</summary>
    public Task SendOffer(SdpMessage message)
    {
        var student = Context.ConnectionId;
        if (string.IsNullOrEmpty(message?.TargetConnectionId)) return Task.CompletedTask;
        if (!_room.IsPaired(message.TargetConnectionId, student)) return Dropped("offer", message.TargetConnectionId);
        return Clients.Client(message.TargetConnectionId).SendAsync(Protocol.ClientMethods.ReceiveOffer,
            message with { FromConnectionId = student, Type = "offer" });
    }

    /// <summary>Teacher (viewer) → student: the SDP answer. Only to a student this teacher is authorised to view.</summary>
    public Task SendAnswer(SdpMessage message)
    {
        var teacher = Context.ConnectionId;
        if (string.IsNullOrEmpty(message?.TargetConnectionId)) return Task.CompletedTask;
        if (!_room.IsPaired(teacher, message.TargetConnectionId)) return Dropped("answer", message.TargetConnectionId);
        return Clients.Client(message.TargetConnectionId).SendAsync(Protocol.ClientMethods.ReceiveAnswer,
            message with { FromConnectionId = teacher, Type = "answer" });
    }

    /// <summary>Relay a single ICE candidate to the other end of an authorised pair (either direction).</summary>
    public Task SendIceCandidate(IceCandidateMessage message)
    {
        var from = Context.ConnectionId;
        var target = message?.TargetConnectionId;
        if (message is null || string.IsNullOrEmpty(target)) return Task.CompletedTask;
        var allowed = (_room.IsTeacher(from) && _room.IsPaired(from, target))
                      || (_room.IsStudent(from) && _room.IsPaired(target, from));
        if (!allowed) return Dropped("ICE candidate", target);

        return Clients.Client(target).SendAsync(Protocol.ClientMethods.ReceiveIceCandidate,
            message with { FromConnectionId = from });
    }

    private Task Dropped(string what, string target)
    {
        _logger.LogDebug("Dropped unauthorised {What} from {From} to {Target}.", what, Context.ConnectionId, target);
        return Task.CompletedTask;
    }

    // ---- telemetry (student -> all teachers) ------------------------------

    public async Task PushTelemetry(TelemetrySnapshot telemetry)
    {
        if (telemetry is null) return;
        var entry = _room.ApplyTelemetry(Context.ConnectionId, telemetry);
        if (entry is null) return;   // not a registered student

        await Clients.Clients(_room.TeacherConnectionIds)
            .SendAsync(Protocol.ClientMethods.TelemetryUpdated, Context.ConnectionId, telemetry);
    }

    // ---- lifecycle --------------------------------------------------------

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var id = Context.ConnectionId;

        if (_room.RemoveStudent(id, out var student))
        {
            _logger.LogInformation("Student left: {Name}", student?.Info.MachineName);
            await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentLeft, id);
            await BroadcastRosterToTeachers();
            if (_polls.Open is not null) await PushPollResultsAsync();
        }
        else if (_room.RemoveTeacher(id, out var watched, out var unlocked))
        {
            _logger.LogInformation("Teacher left: {Id}", id);

            // Fail-safe: a teacher that leaves (or whose app crashes) releases its screen locks,
            // and when no teacher is left the room's limits are lifted — a lab is never left
            // locked or restricted with nobody able to undo it.
            var unlock = new ClassroomCommand { Kind = CommandKind.Unlock };
            foreach (var s in unlocked)
            {
                await Clients.Client(s).SendAsync(Protocol.ClientMethods.CommandReceived, id, unlock);
                await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentLockChanged, s, false);
            }
            if (_room.TeacherCount == 0) await ClosePollAsync();
            if (_room.TeacherCount == 0 && !_room.Policy.IsEmpty)
            {
                _room.Policy = new ClassPolicy();
                await PushPolicyAsync();
                _logger.LogInformation("Last teacher left: room limits cleared.");
            }

            // Tell each agent this teacher was watching to stop encoding for it right away,
            // instead of waiting for WebRTC to notice the dead peer (FR-STREAM-06, NFR-REL-03).
            foreach (var s in watched)
            {
                await Clients.Client(s).SendAsync(Protocol.ClientMethods.StreamStopRequested, id);
                if (_room.ViewerCount(s) == 0)
                    await SetStateAsync(s, StudentState.Online);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task SetStateAsync(string student, StudentState state)
    {
        if (_room.SetStudentState(student, state))
            await Clients.Clients(_room.TeacherConnectionIds)
                .SendAsync(Protocol.ClientMethods.StudentStateChanged, student, state);
    }

    private Task BroadcastRosterToTeachers() =>
        Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.RosterUpdated, _room.BuildRoster());

    /// <summary>
    /// Reject a client whose wire-protocol version doesn't match this server's (FR-DISC-06).
    /// Throwing <see cref="HubException"/> fails the registration call on the client; nothing
    /// is added to the roster, so an incompatible build can't half-join.
    /// </summary>
    private void RejectIfIncompatible(int clientProtocolVersion)
    {
        if (clientProtocolVersion != Protocol.Version)
        {
            _logger.LogWarning("Rejected client with protocol v{Client} (server v{Server}).",
                clientProtocolVersion, Protocol.Version);
            throw new HubException(
                $"Protocol version mismatch: client v{clientProtocolVersion}, server v{Protocol.Version}. Update to a matching build.");
        }
    }
}
