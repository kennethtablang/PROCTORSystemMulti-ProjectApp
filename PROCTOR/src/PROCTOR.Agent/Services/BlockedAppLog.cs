using PROCTOR.Shared.Contracts;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Recently closed blocked programs, for the teacher's activity log and alerts: telemetry carries
/// the last few (name + when), and the teacher app keeps each one once.
/// </summary>
internal static class BlockedAppLog
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();
    private static readonly List<BlockedAppEvent> Events = new();

    public static void Record(string name)
    {
        lock (Gate)
        {
            // A program that restarts itself is killed every pass; one event per name per 10 seconds is enough.
            var now = DateTimeOffset.UtcNow;
            if (Events.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && now - e.At < TimeSpan.FromSeconds(10))) return;
            Events.Add(new BlockedAppEvent { Name = name, At = now });
            Trim();
        }
    }

    public static IReadOnlyList<BlockedAppEvent> Recent()
    {
        lock (Gate)
        {
            Trim();
            return Events.ToArray();
        }
    }

    private static void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow - Keep;
        Events.RemoveAll(e => e.At < cutoff);
        if (Events.Count > BlockedAppEvent.MaxReported) Events.RemoveRange(0, Events.Count - BlockedAppEvent.MaxReported);
    }
}
