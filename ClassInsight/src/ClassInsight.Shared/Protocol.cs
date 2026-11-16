namespace ClassInsight.Shared;

/// <summary>
/// Wire-protocol constants shared by the student agent, the teacher app and the
/// coordination server. Everything on the LAN agrees on these values so that
/// agents can be auto-discovered and "absorbed" without any manual configuration.
/// </summary>
public static class Protocol
{
    /// <summary>Bump when a breaking change is made to any contract below.</summary>
    public const int Version = 1;

    /// <summary>Magic token prefixing every discovery datagram so we ignore stray UDP traffic.</summary>
    public const string DiscoveryMagic = "CLASSINSIGHT/1";

    /// <summary>
    /// UDP port the server listens on for discovery probes and where it emits
    /// periodic beacons. Must be identical on every machine on the LAN.
    /// </summary>
    public const int DiscoveryPort = 47800;

    /// <summary>Default Kestrel HTTP port for the SignalR coordination endpoint.</summary>
    public const int DefaultServerPort = 5080;

    /// <summary>SignalR hub route. Clients connect to http://{ip}:{port}{HubPath}.</summary>
    public const string HubPath = "/hub/classroom";

    /// <summary>How often (ms) the server emits an unsolicited discovery beacon.</summary>
    public const int BeaconIntervalMs = 2000;

    /// <summary>
    /// HTTP status endpoint (FR-SRV-03). Also how a client resolves a manually entered server
    /// address on networks where UDP broadcast is blocked (NFR-COMPAT-02).
    /// </summary>
    public const string StatusPath = "/status";

    /// <summary>Names of hub methods the SERVER invokes ON clients.</summary>
    public static class ClientMethods
    {
        public const string RosterUpdated = nameof(RosterUpdated);
        public const string StudentJoined = nameof(StudentJoined);
        public const string StudentLeft = nameof(StudentLeft);
        public const string StudentStateChanged = nameof(StudentStateChanged);

        // WebRTC signalling relayed between a teacher (viewer) and a student (sender).
        public const string StreamRequested = nameof(StreamRequested);
        public const string StreamStopRequested = nameof(StreamStopRequested);
        public const string ReceiveOffer = nameof(ReceiveOffer);
        public const string ReceiveAnswer = nameof(ReceiveAnswer);
        public const string ReceiveIceCandidate = nameof(ReceiveIceCandidate);

        // Live telemetry fan-out.
        public const string TelemetryUpdated = nameof(TelemetryUpdated);

        // Classroom control (P5): a teacher's command delivered to a student agent, and the
        // resulting lock state pushed to every teacher.
        public const string CommandReceived = nameof(CommandReceived);
        public const string StudentLockChanged = nameof(StudentLockChanged);

        /// <summary>Server → students and teachers: the room's current web/app limits.</summary>
        public const string PolicyUpdated = nameof(PolicyUpdated);

        /// <summary>Server → teachers: a student raised or lowered their hand.</summary>
        public const string StudentHandChanged = nameof(StudentHandChanged);

        /// <summary>Server → a student: a teacher acknowledged (lowered) their raised hand.</summary>
        public const string HandLowered = nameof(HandLowered);

        // Polls: students get the question and its closing; teachers get live results.
        public const string PollStarted = nameof(PollStarted);
        public const string PollClosed = nameof(PollClosed);
        public const string PollResultsUpdated = nameof(PollResultsUpdated);

        // Screen broadcast (teacher → students). Separate from the student-view signalling above:
        // here the teacher is the media sender/offerer and the student answers.
        public const string BroadcastStarted = nameof(BroadcastStarted);
        public const string BroadcastStopped = nameof(BroadcastStopped);
        public const string BroadcastStudentJoined = nameof(BroadcastStudentJoined);
        public const string ReceiveBroadcastOffer = nameof(ReceiveBroadcastOffer);
        public const string ReceiveBroadcastAnswer = nameof(ReceiveBroadcastAnswer);
        public const string ReceiveBroadcastIce = nameof(ReceiveBroadcastIce);

        /// <summary>Server → students and teachers: the class countdown started, changed or stopped.</summary>
        public const string TimerUpdated = nameof(TimerUpdated);

        /// <summary>Server → one student: your screen is now (true) / no longer (false) being shown to the class.</summary>
        public const string ScreenShownToClass = nameof(ScreenShownToClass);

        /// <summary>Server → a student and the teachers: one chat line.</summary>
        public const string ChatReceived = nameof(ChatReceived);
    }

    /// <summary>Names of hub methods CLIENTS invoke ON the server.</summary>
    public static class ServerMethods
    {
        public const string RegisterStudent = nameof(RegisterStudent);
        public const string RegisterTeacher = nameof(RegisterTeacher);
        public const string GetRoster = nameof(GetRoster);

        /// <summary>STUN/TURN servers the room is configured with (relay fallback, P4).</summary>
        public const string GetIceServers = nameof(GetIceServers);

        public const string RequestStream = nameof(RequestStream);
        public const string StopStream = nameof(StopStream);
        public const string SendOffer = nameof(SendOffer);
        public const string SendAnswer = nameof(SendAnswer);
        public const string SendIceCandidate = nameof(SendIceCandidate);

        public const string PushTelemetry = nameof(PushTelemetry);

        /// <summary>Teacher → one, several or all students: message / lock / unlock / open URL.</summary>
        public const string SendCommand = nameof(SendCommand);

        /// <summary>Teacher → server: set the room-wide web/app limits (empty policy clears them).</summary>
        public const string SetPolicy = nameof(SetPolicy);

        /// <summary>Student → server: raise (true) or lower (false) their hand to ask for help.</summary>
        public const string RaiseHand = nameof(RaiseHand);

        /// <summary>Teacher → server: acknowledge a student's raised hand.</summary>
        public const string LowerHand = nameof(LowerHand);

        /// <summary>Teacher → server: put a poll to the class (replaces any open poll). Returns it with its id.</summary>
        public const string StartPoll = nameof(StartPoll);

        /// <summary>Teacher → server: close the open poll.</summary>
        public const string ClosePoll = nameof(ClosePoll);

        /// <summary>Student → server: answer (or change the answer to) the open poll.</summary>
        public const string SubmitPollAnswer = nameof(SubmitPollAnswer);

        /// <summary>Teacher → server: start showing my screen; returns the students to connect to.</summary>
        public const string StartBroadcast = nameof(StartBroadcast);
        public const string StopBroadcast = nameof(StopBroadcast);
        public const string SendBroadcastOffer = nameof(SendBroadcastOffer);
        public const string SendBroadcastAnswer = nameof(SendBroadcastAnswer);
        public const string SendBroadcastIce = nameof(SendBroadcastIce);

        /// <summary>Teacher → server: start (or replace) / stop the class countdown.</summary>
        public const string StartTimer = nameof(StartTimer);
        public const string StopTimer = nameof(StopTimer);

        /// <summary>Teacher → server: send Wake-on-LAN packets to switched-off lab PCs.</summary>
        public const string WakeComputers = nameof(WakeComputers);

        /// <summary>Teacher → one student, or student → teachers: a chat line.</summary>
        public const string SendChat = nameof(SendChat);
    }
}
