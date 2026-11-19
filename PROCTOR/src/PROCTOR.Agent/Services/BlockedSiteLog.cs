using PROCTOR.Shared.Contracts;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Recently closed blocked sites, for the teacher's activity log: telemetry carries the last few
/// (host + when), and the teacher app keeps each one once. Only the host, never the full address.
/// </summary>
internal static class BlockedSiteLog
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();
    private static readonly List<BlockedSiteEvent> Events = new();

    public static void Record(string host)
    {
        lock (Gate)
        {
            Events.Add(new BlockedSiteEvent { Host = host, At = DateTimeOffset.UtcNow });
            Trim();
        }
    }

    public static IReadOnlyList<BlockedSiteEvent> Recent()
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
        if (Events.Count > BlockedSiteEvent.MaxReported) Events.RemoveRange(0, Events.Count - BlockedSiteEvent.MaxReported);
    }
}
