using System.Collections.Concurrent;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Server.Services;

/// <summary>
/// The authoritative in-memory roster for the room: which students and teachers are
/// connected, their last telemetry, and which teacher↔student viewing pairs the server has
/// authorised. Thread-safe; the hub mutates it from many connection callbacks at once.
///
/// There is exactly one room per server process (one lab); several rooms on one network are
/// several server processes (FR-SRV-05). Nothing here is persisted (AD-6, NFR-PRIV-02).
/// </summary>
public sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, StudentEntry> _students = new();   // key: connectionId
    private readonly ConcurrentDictionary<string, TeacherEntry> _teachers = new();   // key: connectionId

    // Authorised viewing pairs (FR-SEC-04). Signalling is relayed only between the two ends of
    // a pair, and a student's viewer count is the number of pairs naming it (FR-STREAM-06).
    private readonly ConcurrentDictionary<ViewPair, byte> _pairs = new();

    public readonly record struct ViewPair(string Teacher, string Student);

    public sealed class StudentEntry
    {
        public required string ConnectionId { get; init; }
        public required StudentInfo Info { get; init; }
        public StudentState State { get; set; } = StudentState.Online;
        public TelemetrySnapshot? LastTelemetry { get; set; }
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

        /// <summary>Teachers currently holding this student's screen locked (FR-CTRL-01). Guarded by itself.</summary>
        public HashSet<string> LockedBy { get; } = new(StringComparer.Ordinal);
        public bool IsLocked { get { lock (LockedBy) return LockedBy.Count > 0; } }

        public bool HandRaised { get; set; }

        public StudentSnapshot ToSnapshot() => new()
        {
            ConnectionId = ConnectionId,
            MachineId = Info.MachineId,
            MachineName = Info.MachineName,
            UserName = Info.UserName,
            IpAddress = Info.IpAddress,
            ScreenWidth = Info.ScreenWidth,
            ScreenHeight = Info.ScreenHeight,
            Displays = Info.Displays,
            MacAddresses = Info.MacAddresses,
            State = State,
            ConnectedAt = ConnectedAt,
            LastTelemetry = LastTelemetry,
            IsLocked = IsLocked,
            HandRaised = HandRaised,
        };
    }

    public sealed class TeacherEntry
    {
        public required string ConnectionId { get; init; }
        public required TeacherInfo Info { get; init; }
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    }

    // ---- students ---------------------------------------------------------

    public StudentEntry AddOrUpdateStudent(string connectionId, StudentInfo info)
    {
        var entry = new StudentEntry { ConnectionId = connectionId, Info = info };
        _students[connectionId] = entry;
        return entry;
    }

    public bool TryGetStudent(string connectionId, out StudentEntry entry)
        => _students.TryGetValue(connectionId, out entry!);

    /// <summary>Remove a student and purge every viewing pair that names it (FR-SRV-04).</summary>
    public bool RemoveStudent(string connectionId, out StudentEntry? entry)
    {
        var removed = _students.TryRemove(connectionId, out var e);
        entry = e;
        foreach (var pair in _pairs.Keys.Where(p => p.Student == connectionId).ToArray())
            _pairs.TryRemove(pair, out _);
        return removed;
    }

    /// <summary>Set a student's state. Returns true only if it actually changed (so callers broadcast once).</summary>
    public bool SetStudentState(string connectionId, StudentState state)
    {
        if (!_students.TryGetValue(connectionId, out var entry)) return false;
        if (entry.State == state) return false;
        entry.State = state;
        return true;
    }

    public StudentEntry? ApplyTelemetry(string connectionId, TelemetrySnapshot telemetry)
    {
        if (!_students.TryGetValue(connectionId, out var entry)) return null;
        entry.LastTelemetry = telemetry;
        return entry;
    }

    public bool IsStudent(string connectionId) => _students.ContainsKey(connectionId);

    public int StudentCount => _students.Count;

    // ---- teachers ---------------------------------------------------------

    public void AddOrUpdateTeacher(string connectionId, TeacherInfo info)
        => _teachers[connectionId] = new TeacherEntry { ConnectionId = connectionId, Info = info };

    /// <summary>
    /// Remove a teacher and purge its viewing pairs (FR-SRV-04). Returns the students it was
    /// watching so the caller can tell those agents to stop and update their state.
    /// </summary>
    public bool RemoveTeacher(string connectionId, out IReadOnlyList<string> watchedStudents)
        => RemoveTeacher(connectionId, out watchedStudents, out _);

    /// <summary>
    /// Remove a teacher, purge its viewing pairs and release its screen locks. Returns the students
    /// it was watching and the students that are now fully unlocked (no teacher holds them), so the
    /// caller can stop those streams and unlock those screens — a crashed teacher app can never
    /// leave a lab locked.
    /// </summary>
    public bool RemoveTeacher(string connectionId, out IReadOnlyList<string> watchedStudents, out IReadOnlyList<string> unlockedStudents)
    {
        var removed = _teachers.TryRemove(connectionId, out _);
        var watched = new List<string>();
        foreach (var pair in _pairs.Keys.Where(p => p.Teacher == connectionId).ToArray())
            if (_pairs.TryRemove(pair, out _)) watched.Add(pair.Student);
        watchedStudents = watched;

        var unlocked = new List<string>();
        foreach (var student in _students.Values)
        {
            lock (student.LockedBy)
            {
                if (student.LockedBy.Remove(connectionId) && student.LockedBy.Count == 0)
                    unlocked.Add(student.ConnectionId);
            }
        }
        unlockedStudents = unlocked;
        return removed;
    }

    // ---- classroom control (P5) --------------------------------------------

    /// <summary>The room's current web/app limits (room-wide, in memory only).</summary>
    public ClassPolicy Policy { get; set; } = new();

    /// <summary>Lock a student's screen on behalf of a teacher. Returns true if it was unlocked before.</summary>
    public bool LockStudent(string student, string teacher)
    {
        if (!_students.TryGetValue(student, out var entry)) return false;
        lock (entry.LockedBy)
        {
            var wasUnlocked = entry.LockedBy.Count == 0;
            entry.LockedBy.Add(teacher);
            return wasUnlocked;
        }
    }

    /// <summary>Release every lock on a student (any teacher may unlock). Returns true if it was locked.</summary>
    public bool UnlockStudent(string student)
    {
        if (!_students.TryGetValue(student, out var entry)) return false;
        lock (entry.LockedBy)
        {
            var wasLocked = entry.LockedBy.Count > 0;
            entry.LockedBy.Clear();
            return wasLocked;
        }
    }

    /// <summary>Set a student's raised-hand flag. Returns true if it changed.</summary>
    public bool SetHand(string student, bool raised)
    {
        if (!_students.TryGetValue(student, out var entry)) return false;
        if (entry.HandRaised == raised) return false;
        entry.HandRaised = raised;
        return true;
    }

    /// <summary>Connection ids of every registered student.</summary>
    public IReadOnlyCollection<string> StudentConnectionIds => _students.Keys.ToArray();

    public bool IsTeacher(string connectionId) => _teachers.ContainsKey(connectionId);

    public IReadOnlyCollection<string> TeacherConnectionIds => _teachers.Keys.ToArray();

    public int TeacherCount => _teachers.Count;

    // ---- viewing pairs (pair-gated signalling, FR-SEC-04) -----------------

    /// <summary>Authorise <paramref name="teacher"/> to view <paramref name="student"/>. Both must be registered.</summary>
    public bool AuthorizePair(string teacher, string student)
    {
        if (!IsTeacher(teacher) || !IsStudent(student)) return false;
        _pairs[new ViewPair(teacher, student)] = 0;
        return true;
    }

    /// <summary>Revoke a pair. Returns true if it existed.</summary>
    public bool RevokePair(string teacher, string student) => _pairs.TryRemove(new ViewPair(teacher, student), out _);

    public bool IsPaired(string teacher, string student) => _pairs.ContainsKey(new ViewPair(teacher, student));

    /// <summary>How many teachers are currently authorised to view this student.</summary>
    public int ViewerCount(string student) => _pairs.Keys.Count(p => p.Student == student);

    public int PairCount => _pairs.Count;

    // ---- projection -------------------------------------------------------

    public RosterSnapshot BuildRoster() => new()
    {
        Students = _students.Values
            .OrderBy(s => s.Info.MachineName, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.ToSnapshot())
            .ToArray(),
        TeacherCount = _teachers.Count,
        GeneratedAt = DateTimeOffset.UtcNow,
    };
}
