using PROCTOR.Shared.Contracts;

namespace PROCTOR.Server.Services;

/// <summary>The class countdown (lesson timer): one at a time, in memory only. Thread-safe.</summary>
public sealed class TimerState
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private string? _label;
    private DateTimeOffset _endsAt;
    private int _total;
    private bool _running;

    public TimerState() : this(TimeProvider.System) { }
    public TimerState(TimeProvider clock) => _clock = clock;

    public void Start(string? label, int seconds)
    {
        lock (_gate)
        {
            _label = label;
            _total = seconds;
            _endsAt = _clock.GetUtcNow().AddSeconds(seconds);
            _running = true;
        }
    }

    /// <summary>Stop the countdown. Returns true if one was running.</summary>
    public bool Stop()
    {
        lock (_gate)
        {
            var was = _running;
            _running = false;
            return was;
        }
    }

    /// <summary>The countdown as it should be shown right now (not running once it has expired).</summary>
    public LessonTimer Snapshot()
    {
        lock (_gate)
        {
            if (!_running) return new LessonTimer();
            var remaining = (int)Math.Ceiling((_endsAt - _clock.GetUtcNow()).TotalSeconds);
            if (remaining <= 0) { _running = false; return new LessonTimer(); }
            return new LessonTimer { Running = true, Label = _label, RemainingSeconds = remaining, TotalSeconds = _total };
        }
    }
}
