using PROCTOR.Server.Services;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace PROCTOR.Server.Hubs;

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
    private readonly BroadcastState _broadcast;
    private readonly TimerState _timer;
    private readonly WakeSender _wake;
    private readonly ChatThrottle _chat;
    private readonly FileTransfers _files;
    private readonly RemoteControlState _remote;
    private readonly AgentStore _agents;
    private readonly WebLimitsStore _webLimits;
    private const string NonceItem = "enrol-nonce";
    private readonly ILogger<ClassroomHub> _logger;

    public ClassroomHub(
        RoomRegistry room,
        ServerIdentity identity,
        TeacherAuthThrottle throttle,
        PollBox polls,
        BroadcastState broadcast,
        TimerState timer,
        WakeSender wake,
        ChatThrottle chat,
        FileTransfers files,
        RemoteControlState remote,
        AgentStore agents,
        WebLimitsStore webLimits,
        ILogger<ClassroomHub> logger)
    {
        _webLimits = webLimits;
        _agents = agents;
        _remote = remote;
        _files = files;
        _chat = chat;
        _wake = wake;
        _timer = timer;
        _broadcast = broadcast;
        _polls = polls;
        _room = room;
        _identity = identity;
        _throttle = throttle;
        _logger = logger;
    }

    // ---- registration -----------------------------------------------------

    /// <summary>
    /// Step 1 of a student registering (NFR-SEC-06): a single-use nonce the agent signs with its own
    /// key. Kept on this connection only, and consumed by the next RegisterStudent.
    /// </summary>
    public EnrollmentChallenge GetEnrollmentChallenge()
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Context.Items[NonceItem] = nonce;
        return new EnrollmentChallenge { Nonce = nonce, KeyRequired = _agents.RequireKey };
    }

    /// <summary>
    /// A student agent announces itself. No login for students (FR-SEC-01), but the agent must prove
    /// which PC it is (NFR-SEC-06): it signs the challenge, bound to this server's TLS key, with its
    /// own key, and a PC id can only ever register with the key it first enrolled with.
    /// </summary>
    public async Task RegisterStudent(StudentInfo info)
    {
        RejectIfIncompatible(info.ProtocolVersion);
        if (_room.IsTeacher(Context.ConnectionId))
            throw new HubException("This connection is already registered as a teacher.");
        VerifyAgent(info);

        // A connection's identity is fixed once registered: re-registering under a different
        // install id (e.g. to answer a poll several times) is refused.
        if (_room.TryGetStudent(Context.ConnectionId, out var already)
            && !string.Equals(already.Info.MachineId, info.MachineId, StringComparison.Ordinal))
            throw new HubException("This connection is already registered as a different student.");

        // Prefer the real socket address the server sees over whatever the agent guessed.
        // (IPv4 clients arrive as IPv4-mapped IPv6, e.g. ::ffff:192.168.1.8; show them as plain IPv4.)
        var remote = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true }) remote = remote.MapToIPv4();
        var ip = remote?.ToString();
        if (!string.IsNullOrWhiteSpace(ip))
            info = info with { IpAddress = ip };

        // Bound what an agent can make every teacher render.
        info = info with
        {
            Displays = info.Displays.Take(16).ToArray(),
            MacAddresses = (info.MacAddresses ?? [])
                .Select(WakeOnLan.Normalize).OfType<string>()
                .Distinct(StringComparer.Ordinal).Take(WakeOnLan.MaxPerMachine).ToArray(),
        };

        var entry = _room.AddOrUpdateStudent(Context.ConnectionId, info);
        _logger.LogInformation("Student joined: {Name} ({Ip})", info.MachineName, info.IpAddress);

        await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.StudentJoined, Snapshot(entry));
        await BroadcastRosterToTeachers();

        // A student who joins mid-lesson gets the room's current limits straight away (with this
        // PC's own website limits, if it has any).
        var effective = EffectivePolicy(info.MachineId);
        if (!effective.IsEmpty)
            await Clients.Caller.SendAsync(Protocol.ClientMethods.PolicyUpdated, effective);

        // ...the class countdown, if one is running...
        if (_timer.Snapshot() is { Running: true } countdown)
            await Clients.Caller.SendAsync(Protocol.ClientMethods.TimerUpdated, countdown);

        // ...the teacher's screen, if it's being shown to the whole class...
        if (_broadcast.AddLateJoiner(Context.ConnectionId) && _broadcast.Teacher is { } broadcaster)
        {
            await Clients.Caller.SendAsync(Protocol.ClientMethods.BroadcastStarted,
                new BroadcastInfo { TeacherConnectionId = broadcaster, Mode = _broadcast.Mode, ShowingStudentName = _broadcast.SourceName });
            await Clients.Client(broadcaster).SendAsync(Protocol.ClientMethods.BroadcastStudentJoined, Context.ConnectionId);
        }

        // ...and the open poll, if there is one.
        if (_polls.Open is { } poll)
        {
            await Clients.Caller.SendAsync(Protocol.ClientMethods.PollStarted, poll);
            await PushPollResultsAsync();
        }
    }

    private void VerifyAgent(StudentInfo info)
    {
        if (!Context.Items.Remove(NonceItem, out var item) || item is not string nonce)
            throw new HubException("Ask for an enrolment challenge before registering.");
        if (string.IsNullOrEmpty(info.MachineId) || info.MachineId.Length > 64)
            throw new HubException("Missing PC id.");

        byte[] publicKey, signature;
        try
        {
            publicKey = Convert.FromBase64String(info.PublicKey ?? "");
            signature = Convert.FromBase64String(info.Signature ?? "");
        }
        catch (FormatException)
        {
            throw new HubException("This PC's identity couldn't be verified.");
        }
        if (publicKey.Length is 0 or > 512 || signature.Length is 0 or > 256
            || !AgentEnrollment.Verify(publicKey, signature, _identity.Fingerprint, nonce, info.MachineId))
            throw new HubException("This PC's identity couldn't be verified.");

        var keyId = AgentEnrollment.KeyId(publicKey);
        var remote = Context.GetHttpContext()?.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true }) remote = remote.MapToIPv4();
        switch (_agents.Check(info.MachineId, keyId, info.EnrollmentProof, ClassroomLimits.CleanText(info.MachineName) ?? "?", remote?.ToString() ?? "unknown"))
        {
            case AgentStore.Outcome.RateLimited:
                throw new HubException("Too many new PCs are enrolling right now; try again later.");
            case AgentStore.Outcome.KeyMismatch:
                _logger.LogWarning("Refused {Name} ({Ip}): PC id {Id} is enrolled with a different key (impersonation, or a re-imaged PC — delete its entry in agents.json to re-enrol).",
                    info.MachineName, Context.GetHttpContext()?.Connection.RemoteIpAddress, info.MachineId);
                throw new HubException("This PC id is enrolled with a different key.");
            case AgentStore.Outcome.EnrollmentKeyRequired:
                _logger.LogWarning("Refused {Name} ({Ip}): new PC without a valid lab enrolment key.", info.MachineName, remote);
                throw new HubException("This PC isn't enrolled: install the agent with the lab's enrolment key.");
            case AgentStore.Outcome.Full:
                throw new HubException("The server can't enrol more PCs.");
            case AgentStore.Outcome.Enrolled:
                _logger.LogInformation("Enrolled a new PC: {Name} (key {Key}).", info.MachineName, ServerTrust.Short(keyId));
                break;
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
        await Clients.Caller.SendAsync(Protocol.ClientMethods.RosterUpdated, Roster());
        await Clients.Caller.SendAsync(Protocol.ClientMethods.PolicyUpdated, _room.Policy);
        if (_polls.Results(_room.StudentCount) is { } results)
            await Clients.Caller.SendAsync(Protocol.ClientMethods.PollResultsUpdated, results);
        await Clients.Caller.SendAsync(Protocol.ClientMethods.TimerUpdated, _timer.Snapshot());
    }

    // ---- chat ------------------------------------------------------------------

    /// <summary>
    /// Teacher → one student, or student → the teachers. Every teacher sees every conversation (so
    /// two teachers in a room stay in step); a student only ever sees their own. Relayed, never stored.
    /// </summary>
    public async Task SendChat(ChatSend send)
    {
        var me = Context.ConnectionId;
        var teacher = _room.IsTeacher(me);
        if (!teacher && !_room.IsStudent(me)) throw new HubException("Register first.");
        var text = ClassroomLimits.CleanText(send?.Text) ?? throw new HubException("A message needs some text.");
        if (!_chat.Allow(me, teacher)) throw new HubException("You're sending messages too quickly. Wait a few seconds.");

        ChatMessage message;
        if (teacher)
        {
            var student = send!.ToConnectionId;
            if (string.IsNullOrEmpty(student) || !_room.IsStudent(student)) throw new HubException("That student isn't connected.");
            message = new ChatMessage { StudentConnectionId = student, FromTeacher = true, FromName = "Teacher", Text = text };
            message = message with { Id = Guid.NewGuid().ToString("N"), SentAt = DateTimeOffset.UtcNow };
            await Clients.Client(student).SendAsync(Protocol.ClientMethods.ChatReceived, message);
        }
        else
        {
            var name = StudentName(me);
            message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                StudentConnectionId = me,
                FromTeacher = false,
                FromName = name,
                Text = text,
                SentAt = DateTimeOffset.UtcNow,
            };
        }
        await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.ChatReceived, message);
    }

    private string StudentName(string connectionId) =>
        _room.TryGetStudent(connectionId, out var entry) && entry is not null
            ? entry.Info.UserName is { Length: > 0 } user ? $"{entry.Info.MachineName} · {user}" : entry.Info.MachineName
            : "Student";

    // ---- remote control (FR-CTRL-05) ------------------------------------------

    /// <summary>
    /// Take control of a student's keyboard and mouse. A strong control: refused in open mode unless
    /// IT opted in, and refused when the PC has it turned off. One controller per student (a second
    /// teacher takes over and the first is told). The student sees a banner the whole time.
    /// </summary>
    public async Task StartRemoteControl(string studentConnectionId)
    {
        var teacher = Context.ConnectionId;
        if (!_room.IsTeacher(teacher)) throw new HubException("Only teachers can take control.");
        if (StrongControlsBlocked)
            throw new HubException("Remote control needs a teacher join code on this server (it runs in open mode).");
        if (string.IsNullOrEmpty(studentConnectionId) || !_room.TryGetStudent(studentConnectionId, out var student) || student is null)
            throw new HubException("That student isn't connected.");
        if (!student.Info.RemoteControlAllowed) throw new HubException("Remote control is turned off on that PC.");

        if (_remote.Take(studentConnectionId, teacher) is { } previous)
            await Clients.Client(previous).SendAsync(Protocol.ClientMethods.RemoteControlEnded, studentConnectionId);
        await Clients.Client(studentConnectionId).SendAsync(Protocol.ClientMethods.RemoteControlChanged,
            new RemoteControlInfo { TeacherConnectionId = teacher, Active = true });
        _logger.LogInformation("Teacher {Teacher} took control of {Student}.", teacher, student.Info.MachineName);
    }

    public async Task StopRemoteControl(string studentConnectionId)
    {
        var teacher = Context.ConnectionId;
        if (!_remote.Release(studentConnectionId ?? "", teacher)) return;
        await Clients.Client(studentConnectionId!).SendAsync(Protocol.ClientMethods.RemoteControlChanged,
            new RemoteControlInfo { TeacherConnectionId = teacher, Active = false });
        _logger.LogInformation("Teacher {Teacher} released control of {Student}.", teacher, studentConnectionId);
    }

    /// <summary>Relay input — only from the teacher holding control, validated, rate-limited.</summary>
    public async Task SendRemoteInput(RemoteInputBatch batch)
    {
        var teacher = Context.ConnectionId;
        if (batch is null || !_remote.IsController(batch.StudentConnectionId ?? "", teacher))
            throw new HubException("You're not controlling that PC.");
        if (batch.Events is null || batch.Events.Count > RemoteControlLimits.MaxEventsPerBatch || !batch.Events.All(RemoteControlLimits.IsValid))
            throw new HubException("Invalid input.");
        if (!_remote.AllowBatch(teacher, RemoteControlLimits.MaxBatchesPerSecond)) return;   // over the rate: drop
        await Clients.Client(batch.StudentConnectionId!).SendAsync(Protocol.ClientMethods.RemoteInputReceived,
            batch with { DisplayIndex = Math.Clamp(batch.DisplayIndex, 0, 15) });
    }

    // ---- file transfer --------------------------------------------------------

    /// <summary>
    /// Announce a file. A teacher sends to students (executables only with a join code); a student
    /// can only hand work in to the teacher who is collecting it (never executables, at most
    /// <see cref="FileLimits.MaxHandInFilesPerStudent"/> files). Returns the offer with its transfer
    /// id and the receivers; the bytes follow with <see cref="SendFileChunk"/>.
    /// </summary>
    public async Task<FileOffer> OfferFile(FileOffer offer)
    {
        var me = Context.ConnectionId;
        var teacher = _room.IsTeacher(me);
        if (!teacher && !_room.IsStudent(me)) throw new HubException("Register first.");
        if (offer is null) throw new HubException("No file.");
        var name = FileLimits.CleanFileName(offer.FileName) ?? throw new HubException("That file name can't be used.");
        if (offer.Size < 0 || offer.Size > FileLimits.MaxSize)
            throw new HubException($"Files can be at most {FileLimits.MaxSize / (1024 * 1024)} MB.");

        string[] targets;
        string fromName;
        if (teacher)
        {
            if (offer.HandIn) throw new HubException("Only students hand work in.");
            if (FileLimits.IsExecutable(name) && StrongControlsBlocked)
                throw new HubException("Programs and scripts can only be sent with a teacher join code on this server (it runs in open mode).");
            targets = ResolveTargets(offer.Targets).ToArray();
            if (targets.Length == 0) throw new HubException("No students to send to.");
            fromName = "Teacher";
        }
        else
        {
            if (!offer.HandIn) throw new HubException("Students can only hand work in.");
            if (FileLimits.IsExecutable(name)) throw new HubException("Programs and scripts can't be handed in.");
            if (!_files.IsCollecting(me)) throw new HubException("Your teacher isn't collecting work right now.");
            var collector = _files.ReserveHandIn(me)
                ?? throw new HubException($"You can hand in at most {FileLimits.MaxHandInFilesPerStudent} files.");
            if (!_room.IsTeacher(collector)) throw new HubException("Your teacher isn't connected.");
            targets = [collector];
            fromName = StudentName(me);
        }

        var id = _files.Begin(me, targets, offer.Size)
            ?? throw new HubException("Too many files on the way at once; wait for one to finish.");
        var relayed = new FileOffer
        {
            TransferId = id,
            FileName = name,
            Size = offer.Size,
            HandIn = !teacher,
            OpenOnArrival = teacher && offer.OpenOnArrival,
            FromName = fromName,
            FromConnectionId = me,
        };
        await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.FileOffered, relayed);
        _logger.LogInformation("{From} is sending \"{Name}\" ({Size:N0} bytes) to {Count} receiver(s).", fromName, name, offer.Size, targets.Length);
        return relayed with { Targets = targets };
    }

    public async Task SendFileChunk(FileChunk chunk)
    {
        var targets = _files.Chunk(Context.ConnectionId, chunk) ?? throw new HubException("That file transfer isn't open (or a piece arrived out of order).");
        await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.FileChunkReceived, chunk);
    }

    public async Task FinishFile(FileFinish finish)
    {
        var targets = _files.Finish(Context.ConnectionId, finish?.TransferId ?? "") ?? throw new HubException("That file transfer isn't complete.");
        await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.FileFinished, finish);
    }

    public async Task CancelFile(string transferId)
    {
        if (_files.Cancel(Context.ConnectionId, transferId) is { } targets)
            await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.FileCancelled, transferId);
    }

    /// <summary>A receiver tells the sender whether the file was saved.</summary>
    public async Task ReportFileResult(FileReceipt result)
    {
        var me = Context.ConnectionId;
        if (result is null || _files.Result(me, result.TransferId) is not { } sender) return;
        var name = _room.IsTeacher(me) ? "Teacher" : StudentName(me);
        await Clients.Client(sender).SendAsync(Protocol.ClientMethods.FileResultReceived, result with
        {
            FromConnectionId = me,
            FromName = name,
            Error = ClassroomLimits.CleanText(result.Error),
        });
    }

    /// <summary>Start collecting work from students (empty targets = whole class). Returns how many were asked.</summary>
    public async Task<int> RequestHandIn(HandInRequest request)
    {
        var teacher = Context.ConnectionId;
        if (!_room.IsTeacher(teacher)) throw new HubException("Only teachers can collect work.");
        var (targets, skipped) = _files.OpenHandIn(teacher, ResolveTargets(request?.Targets), _room.IsTeacher);
        if (targets.Length == 0 && skipped > 0)
            throw new HubException("Another teacher is already collecting work from these students.");
        await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.HandInRequested,
            new HandInInfo { TeacherConnectionId = teacher, Prompt = ClassroomLimits.CleanText(request?.Prompt), Open = true });
        return targets.Length;
    }

    public async Task EndHandIn() => await EndHandInOf(Context.ConnectionId);

    private async Task EndHandInOf(string teacher)
    {
        var students = _files.CloseHandIn(teacher);
        if (students.Length > 0)
            await Clients.Clients(students).SendAsync(Protocol.ClientMethods.HandInRequested,
                new HandInInfo { TeacherConnectionId = teacher, Open = false });
    }

    private async Task CleanUpFilesOf(string connectionId)
    {
        foreach (var (id, targets) in _files.RemoveConnection(connectionId))
            await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.FileCancelled, id);
    }

    // ---- wake-on-LAN ----------------------------------------------------------

    /// <summary>
    /// Switch lab PCs on: the server (on the lab network) broadcasts a magic packet for each MAC.
    /// Waking a PC is harmless, so it's allowed in open mode too; it is rate-limited server-wide.
    /// Returns how many computers a packet was sent for.
    /// </summary>
    public async Task<int> WakeComputers(WakeRequest request)
    {
        if (!_room.IsTeacher(Context.ConnectionId)) throw new HubException("Only teachers can wake computers.");
        var macs = (request?.MacAddresses ?? [])
            .Select(m => WakeOnLan.TryParseMac(m, out var mac) ? mac : null).OfType<byte[]>()
            .DistinctBy(Convert.ToHexString).ToList();
        if (macs.Count == 0) throw new HubException("No valid network (MAC) addresses to wake.");
        if (macs.Count > WakeOnLan.MaxPerRequest) throw new HubException($"Wake at most {WakeOnLan.MaxPerRequest} computers at a time.");
        if (!_wake.TryReserve()) throw new HubException("Computers were just woken; wait a few seconds and try again.");
        var sent = await _wake.SendAsync(macs, _logger);
        _logger.LogInformation("Teacher {Teacher} sent Wake-on-LAN to {Count} computer(s).", Context.ConnectionId, sent);
        return sent;
    }

    // ---- lesson timer -----------------------------------------------------

    /// <summary>Start (or replace) the class countdown, shown on every student screen.</summary>
    public async Task<LessonTimer> StartTimer(TimerRequest request)
    {
        if (!_room.IsTeacher(Context.ConnectionId)) throw new HubException("Only teachers can start a timer.");
        if (request is null || request.Seconds < TimerLimits.MinSeconds || request.Seconds > TimerLimits.MaxSeconds)
            throw new HubException($"A timer must run between {TimerLimits.MinSeconds} seconds and {TimerLimits.MaxSeconds / 3600} hours.");
        var label = TimerLimits.CleanLabel(request.Label);
        _timer.Start(label, request.Seconds);
        await PushTimerAsync();
        return _timer.Snapshot();
    }

    public async Task StopTimer()
    {
        if (!_room.IsTeacher(Context.ConnectionId)) throw new HubException("Only teachers can stop the timer.");
        if (_timer.Stop()) await PushTimerAsync();
    }

    private async Task PushTimerAsync()
    {
        var t = _timer.Snapshot();
        await Clients.Clients(_room.StudentConnectionIds).SendAsync(Protocol.ClientMethods.TimerUpdated, t);
        await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.TimerUpdated, t);
    }

    // ---- screen broadcast (FR-CTRL-02) -------------------------------------

    /// <summary>
    /// Start showing the caller's screen to the given students (empty = whole class, including
    /// students who join later). Replaces any other teacher's broadcast. Returns the students the
    /// teacher should now connect to (it sends each an offer; media then flows peer-to-peer).
    /// </summary>
    public async Task<IReadOnlyList<string>> StartBroadcast(BroadcastRequest request)
    {
        var teacher = Context.ConnectionId;
        if (!_room.IsTeacher(teacher)) throw new HubException("Only teachers can show their screen.");
        if (request is null || !Enum.IsDefined(request.Mode)) throw new HubException("Unknown broadcast mode.");

        // Showing a student's screen: it must be a real student, who is never one of the viewers.
        string? source = null, sourceName = null;
        if (!string.IsNullOrEmpty(request.SourceStudentConnectionId))
        {
            if (!_room.TryGetStudent(request.SourceStudentConnectionId, out var shown))
                throw new HubException("That student isn't connected any more.");
            source = request.SourceStudentConnectionId;
            sourceName = shown.Info.MachineName;
        }

        var wholeClass = request.Targets is null || request.Targets.Count == 0;
        var targets = ResolveTargets(request.Targets).Where(t => t != source).ToArray();
        var (previousTeacher, previousTargets, previousSource) = _broadcast.Start(teacher, request.Mode, wholeClass, targets, source, sourceName);
        if (previousTeacher is not null)
            await Clients.Clients(previousTargets).SendAsync(Protocol.ClientMethods.BroadcastStopped, previousTeacher);
        if (previousSource is not null && previousSource != source)
            await Clients.Client(previousSource).SendAsync(Protocol.ClientMethods.ScreenShownToClass, false);

        await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.BroadcastStarted,
            new BroadcastInfo { TeacherConnectionId = teacher, Mode = request.Mode, ShowingStudentName = sourceName });
        // Transparency: the student always knows when their screen is on show.
        if (source is not null)
            await Clients.Client(source).SendAsync(Protocol.ClientMethods.ScreenShownToClass, true);
        _logger.LogInformation("Teacher {Teacher} is showing {Whose} screen ({Mode}) to {Count} student(s).",
            teacher, sourceName is null ? "their" : sourceName + "'s", request.Mode, targets.Length);
        return targets;
    }

    /// <summary>Stop showing the caller's screen; every viewer closes.</summary>
    public async Task StopBroadcast() => await StopBroadcastOf(Context.ConnectionId);

    /// <summary>End <paramref name="teacher"/>'s broadcast (if it has one): viewers close, a shown student is told.</summary>
    private async Task StopBroadcastOf(string teacher)
    {
        var source = _broadcast.Teacher == teacher ? _broadcast.SourceStudent : null;
        if (_broadcast.Stop(teacher) is not { } targets) return;
        await Clients.Clients(targets).SendAsync(Protocol.ClientMethods.BroadcastStopped, teacher);
        if (source is not null)
            await Clients.Client(source).SendAsync(Protocol.ClientMethods.ScreenShownToClass, false);
    }

    /// <summary>Broadcasting teacher → one of its target students: the SDP offer.</summary>
    public Task SendBroadcastOffer(SdpMessage message)
    {
        var teacher = Context.ConnectionId;
        if (message is null || string.IsNullOrEmpty(message.TargetConnectionId) || !_broadcast.IsPair(teacher, message.TargetConnectionId))
            return Dropped("broadcast offer", message?.TargetConnectionId ?? "");
        return Clients.Client(message.TargetConnectionId).SendAsync(Protocol.ClientMethods.ReceiveBroadcastOffer,
            message with { FromConnectionId = teacher, Type = "offer" });
    }

    /// <summary>Target student → the broadcasting teacher: the SDP answer.</summary>
    public Task SendBroadcastAnswer(SdpMessage message)
    {
        var student = Context.ConnectionId;
        if (message is null || string.IsNullOrEmpty(message.TargetConnectionId) || !_broadcast.IsPair(message.TargetConnectionId, student))
            return Dropped("broadcast answer", message?.TargetConnectionId ?? "");
        return Clients.Client(message.TargetConnectionId).SendAsync(Protocol.ClientMethods.ReceiveBroadcastAnswer,
            message with { FromConnectionId = student, Type = "answer" });
    }

    /// <summary>ICE for a broadcast peer, either direction, only within the broadcast.</summary>
    public Task SendBroadcastIce(IceCandidateMessage message)
    {
        var from = Context.ConnectionId;
        var target = message?.TargetConnectionId;
        if (message is null || string.IsNullOrEmpty(target)) return Task.CompletedTask;
        var allowed = _broadcast.IsPair(from, target) || _broadcast.IsPair(target, from);
        if (!allowed) return Dropped("broadcast ICE", target);
        return Clients.Client(target).SendAsync(Protocol.ClientMethods.ReceiveBroadcastIce, message with { FromConnectionId = from });
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
        var key = string.IsNullOrWhiteSpace(student.Info.MachineId) ? id : student.Info.MachineId;
        if (_polls.Submit(key, id, name, answer))
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
        if (command.Kind is CommandKind.LaunchProgram or CommandKind.CloseAllPrograms && StrongControlsBlocked)
            throw new HubException("Opening and closing programs need a teacher join code on this server (it runs in open mode).");

        var text = ClassroomLimits.CleanText(command.Text);
        string? url = null;
        switch (command.Kind)
        {
            case CommandKind.Message when text is null:
                throw new HubException("A message needs some text.");
            case CommandKind.LaunchProgram:
                text = ClassroomLimits.CleanProgram(command.Text)
                    ?? throw new HubException("Give a program name (e.g. Word, chrome.exe) or the full path of an .exe, without options.");
                break;
            case CommandKind.OpenUrl:
                if (!ClassroomLimits.TryCleanUrl(command.Url, out var clean))
                    throw new HubException("Only http:// or https:// web addresses can be opened.");
                url = clean;
                break;
        }

        var targets = ResolveTargets(command.Targets);
        var relayed = new ClassroomCommand
        {
            Kind = command.Kind,
            Text = text,
            Url = url,
            // The lock screen's look (heading and background), validated like everything else.
            Title = command.Kind == CommandKind.Lock ? ClassroomLimits.CleanLockTitle(command.Title) : null,
            Color = command.Kind == CommandKind.Lock ? ClassroomLimits.CleanColor(command.Color) : null,
        };

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
        if (StrongControlsBlocked && (clean.BlockInternet || clean.BlockedApps.Count > 0 || clean.BlockUsbStorage))
            throw new HubException("Turning off the internet, blocking programs and blocking flash drives need a teacher join code on this server (it runs in open mode).");
        _room.Policy = clean;
        // Website limits outlast the lesson (saved to disk) - but not when the strong controls are
        // reserved for teachers with a code: then anyone opening a teacher app could leave the lab
        // stuck on a whitelist, so they end with the lesson as before.
        if (!StrongControlsBlocked) _webLimits.SetClass(clean.Web);
        await PushPolicyAsync();
        _logger.LogInformation("Teacher {Teacher} set limits: web {Mode} ({Hosts} sites), {Apps} blocked apps, internet {Net}, mute {Mute}, flash drives {Usb}, exam {Exam}.",
            Context.ConnectionId, clean.WebMode, clean.WebHosts.Count, clean.BlockedApps.Count,
            clean.BlockInternet ? "off" : "on", clean.MuteSound, clean.BlockUsbStorage ? "blocked" : "allowed", clean.ExamMode);
        return clean;
    }

    /// <summary>
    /// Give selected students their own website limits (a whitelist or blacklist of their own),
    /// replacing the class's for them; null limits put them back on the class's. Kept by PC, so
    /// they follow the PC across reconnects and restarts. Returns how many students it applied to.
    /// </summary>
    public async Task<int> SetStudentWebLimits(StudentWebLimitsRequest request)
    {
        if (!_room.IsTeacher(Context.ConnectionId)) throw new HubException("Only teachers can set limits.");
        if (StrongControlsBlocked)
            throw new HubException("A student's own website list needs a teacher join code on this server (it runs in open mode).");
        if (request?.Students is not { Count: > 0 }) throw new HubException("Choose at least one student.");
        var limits = request.Limits is null ? null : ClassroomLimits.CleanWeb(request.Limits);
        var targets = ResolveTargets(request.Students);
        foreach (var id in targets)
        {
            if (!_room.TryGetStudent(id, out var student) || student is null) continue;
            _webLimits.SetMachine(student.Info.MachineId, limits);
            await Clients.Client(id).SendAsync(Protocol.ClientMethods.PolicyUpdated, EffectivePolicy(student.Info.MachineId));
        }
        await BroadcastRosterToTeachers();
        _logger.LogInformation("Teacher {Teacher} set own website limits for {Count} student(s): {Mode} ({Hosts} sites).",
            Context.ConnectionId, targets.Count, limits?.WebMode.ToString() ?? "class", limits?.WebHosts.Count ?? 0);
        return targets.Count;
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
    /// in with <c>PROCTOR:AllowStrongControlsInOpenMode</c>. With a join code they're always available.
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
        // Each student gets the class policy with its own website limits, if it has any.
        foreach (var id in _room.StudentConnectionIds.ToArray())
            if (_room.TryGetStudent(id, out var student) && student is not null)
                await Clients.Client(id).SendAsync(Protocol.ClientMethods.PolicyUpdated, EffectivePolicy(student.Info.MachineId));
        await Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.PolicyUpdated, _room.Policy);
    }

    /// <summary>The room's policy as one PC sees it: its own website limits replace the class's.</summary>
    private ClassPolicy EffectivePolicy(string? machineId) => _room.Policy.WithWeb(OwnWebLimits(machineId));

    // Saved per-PC lists apply only while the strong controls are open to every teacher (see SetPolicy).
    private WebLimits? OwnWebLimits(string? machineId) => StrongControlsBlocked ? null : _webLimits.ForMachine(machineId);

    private StudentSnapshot Snapshot(RoomRegistry.StudentEntry entry) =>
        entry.ToSnapshot() with { OwnWebLimits = OwnWebLimits(entry.Info.MachineId) };

    private RosterSnapshot Roster()
    {
        var roster = _room.BuildRoster();
        return roster with { Students = roster.Students.Select(s => s with { OwnWebLimits = OwnWebLimits(s.MachineId) }).ToArray() };
    }

    /// <summary>Explicit roster pull (teachers normally rely on the pushed snapshot).</summary>
    public RosterSnapshot GetRoster()
    {
        if (!_room.IsTeacher(Context.ConnectionId))
            throw new HubException("Only teachers can read the roster.");
        return Roster();
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

    /// <summary>The agent package this server offers (agent auto-update), to registered students only.</summary>
    public AgentUpdateInfo? GetAgentUpdate()
    {
        if (!_room.IsStudent(Context.ConnectionId)) return null;
        return Context.GetHttpContext()?.RequestServices.GetService<AgentUpdatePackage>()?.Info;
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
            _polls.RemoveConnection(id);
            _broadcast.RemoveStudent(id);
            _chat.Remove(id);
            await CleanUpFilesOf(id);
            if (_remote.RemoveStudent(id) is { } controller)
                await Clients.Client(controller).SendAsync(Protocol.ClientMethods.RemoteControlEnded, id);
            // The student whose screen was on show left: end that broadcast (viewers would only see a frozen picture).
            if (_broadcast.SourceStudent == id && _broadcast.Teacher is { } showing)
                await StopBroadcastOf(showing);
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
            // A broadcasting teacher that leaves stops the broadcast: no student is left staring at
            // a frozen full-screen viewer.
            await StopBroadcastOf(id);
            _chat.Remove(id);
            await EndHandInOf(id);
            await CleanUpFilesOf(id);
            // Fail-safe: a teacher that leaves releases every PC it was controlling.
            foreach (var s in _remote.RemoveTeacher(id))
                await Clients.Client(s).SendAsync(Protocol.ClientMethods.RemoteControlChanged,
                    new RemoteControlInfo { TeacherConnectionId = id, Active = false });
            if (_room.TeacherCount == 0) await ClosePollAsync();
            if (_room.TeacherCount == 0 && _timer.Stop()) await PushTimerAsync();
            // Programs, internet and sound limits end with the lesson (a crashed teacher app must not
            // leave a lab without internet); website limits stay until a teacher changes them.
            // (With the strong controls reserved for code holders, website limits end too.)
            var policy = _room.Policy;
            var keepWeb = !StrongControlsBlocked;
            if (_room.TeacherCount == 0 && (policy.HasLessonLimits || (!keepWeb && policy.HasWebLimits)))
            {
                _room.Policy = keepWeb ? new ClassPolicy().WithWeb(policy.Web) : new ClassPolicy();
                await PushPolicyAsync();
                _logger.LogInformation(keepWeb
                    ? "Last teacher left: program, internet and sound limits cleared; website limits kept."
                    : "Last teacher left: room limits cleared.");
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
        Clients.Clients(_room.TeacherConnectionIds).SendAsync(Protocol.ClientMethods.RosterUpdated, Roster());

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
