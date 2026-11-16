namespace ClassInsight.Server.Services;

/// <summary>
/// Which teacher controls which student's keyboard and mouse (one controller per student), plus a
/// per-teacher rate limit on input batches. In memory only; thread-safe.
/// </summary>
public sealed class RemoteControlState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _controller = new(StringComparer.Ordinal);   // student → teacher
    private readonly Dictionary<string, (long Second, int Count)> _rate = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public RemoteControlState() : this(TimeProvider.System) { }
    public RemoteControlState(TimeProvider clock) => _clock = clock;

    /// <summary>Give <paramref name="teacher"/> control; returns the teacher it was taken from, if another.</summary>
    public string? Take(string student, string teacher)
    {
        lock (_gate)
        {
            _controller.TryGetValue(student, out var previous);
            _controller[student] = teacher;
            return previous is not null && previous != teacher ? previous : null;
        }
    }

    public bool Release(string student, string teacher)
    {
        lock (_gate)
        {
            if (!_controller.TryGetValue(student, out var t) || t != teacher) return false;
            _controller.Remove(student);
            return true;
        }
    }

    public bool IsController(string student, string teacher)
    {
        lock (_gate) return _controller.TryGetValue(student, out var t) && t == teacher;
    }

    /// <summary>Count one batch for <paramref name="teacher"/>; false when over the per-second limit.</summary>
    public bool AllowBatch(string teacher, int perSecond)
    {
        var second = _clock.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            var (s, n) = _rate.GetValueOrDefault(teacher);
            if (s != second) (s, n) = (second, 0);
            if (n >= perSecond) return false;
            _rate[teacher] = (s, n + 1);
            return true;
        }
    }

    /// <summary>A teacher left: returns the students it was controlling (now released).</summary>
    public string[] RemoveTeacher(string teacher)
    {
        lock (_gate)
        {
            var students = _controller.Where(kv => kv.Value == teacher).Select(kv => kv.Key).ToArray();
            foreach (var s in students) _controller.Remove(s);
            _rate.Remove(teacher);
            return students;
        }
    }

    /// <summary>A student left: returns its controller, if any.</summary>
    public string? RemoveStudent(string student)
    {
        lock (_gate) return _controller.Remove(student, out var t) ? t : null;
    }
}
