using ClassInsight.Shared.Contracts;

namespace ClassInsight.Server.Services;

/// <summary>
/// The room's screen broadcast (FR-CTRL-02): which teacher is showing their screen, how, and to
/// which students. One broadcast at a time; in memory only. It also authorises the broadcast
/// signalling: offers, answers and ICE are relayed only between the broadcasting teacher and its
/// target students (the same pair-gating idea as FR-SEC-04, in the opposite direction).
/// </summary>
public sealed class BroadcastState
{
    private readonly object _gate = new();
    private readonly HashSet<string> _targets = new(StringComparer.Ordinal);

    public string? Teacher { get; private set; }
    public BroadcastMode Mode { get; private set; }

    /// <summary>Whole class: students who join later are added too.</summary>
    public bool WholeClass { get; private set; }

    /// <summary>Start (or replace) the broadcast. Returns the previous broadcast's teacher and targets, if any.</summary>
    public (string? PreviousTeacher, string[] PreviousTargets) Start(string teacher, BroadcastMode mode, bool wholeClass, IEnumerable<string> targets)
    {
        lock (_gate)
        {
            var previous = (Teacher, _targets.ToArray());
            Teacher = teacher;
            Mode = mode;
            WholeClass = wholeClass;
            _targets.Clear();
            foreach (var t in targets) _targets.Add(t);
            return previous;
        }
    }

    /// <summary>Stop the broadcast if <paramref name="teacher"/> owns it. Returns its targets, or null.</summary>
    public string[]? Stop(string teacher)
    {
        lock (_gate)
        {
            if (Teacher != teacher) return null;
            var targets = _targets.ToArray();
            Teacher = null;
            WholeClass = false;
            _targets.Clear();
            return targets;
        }
    }

    /// <summary>A student joined: if the whole class is being shown, add them. Returns true if added.</summary>
    public bool AddLateJoiner(string student)
    {
        lock (_gate)
        {
            if (Teacher is null || !WholeClass) return false;
            return _targets.Add(student);
        }
    }

    public void RemoveStudent(string student)
    {
        lock (_gate) _targets.Remove(student);
    }

    /// <summary>True when <paramref name="teacher"/> is broadcasting to <paramref name="student"/>.</summary>
    public bool IsPair(string teacher, string student)
    {
        lock (_gate) return Teacher == teacher && _targets.Contains(student);
    }

    public string[] Targets { get { lock (_gate) return _targets.ToArray(); } }
}
