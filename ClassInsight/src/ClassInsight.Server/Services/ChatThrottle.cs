using ClassInsight.Shared.Contracts;

namespace ClassInsight.Server.Services;

/// <summary>Per-connection sliding-window rate limit for chat, so one PC can't flood every teacher.</summary>
public sealed class ChatThrottle
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _sent = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public ChatThrottle() : this(TimeProvider.System) { }
    public ChatThrottle(TimeProvider clock) => _clock = clock;

    public bool Allow(string connectionId, bool teacher)
    {
        var limit = teacher ? ChatLimits.TeacherBurst : ChatLimits.StudentBurst;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_sent.TryGetValue(connectionId, out var q)) _sent[connectionId] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= ChatLimits.Window) q.Dequeue();
            if (q.Count >= limit) return false;
            q.Enqueue(now);
            return true;
        }
    }

    public void Remove(string connectionId)
    {
        lock (_gate) _sent.Remove(connectionId);
    }
}
