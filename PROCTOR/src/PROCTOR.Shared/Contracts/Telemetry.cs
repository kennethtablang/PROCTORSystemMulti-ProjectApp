namespace PROCTOR.Shared.Contracts;

/// <summary>
/// A real-time telemetry sample pushed by the student agent on a fixed cadence
/// (independent of the video stream). This is what powers the processes and
/// bandwidth views in the teacher navbar.
/// </summary>
public sealed record TelemetrySnapshot
{
    public string MachineId { get; init; } = string.Empty;

    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.UtcNow;

    public CpuMemorySample System { get; init; } = new();

    public BandwidthSample Bandwidth { get; init; } = new();

    /// <summary>Top processes by resource use. The agent caps this list (e.g. top 15).</summary>
    public IReadOnlyList<ProcessSample> TopProcesses { get; init; } = Array.Empty<ProcessSample>();

    /// <summary>Title of the window currently in the foreground, if resolvable.</summary>
    public string? ForegroundWindowTitle { get; init; }

    /// <summary>Active browser tab URL/host if the agent surfaces it (optional).</summary>
    public string? ActiveUrl { get; init; }

    /// <summary>
    /// USB flash drives and memory cards plugged into the PC (Windows "removable" drives with media
    /// in them), at most <see cref="RemovableDrive.MaxReported"/>. Empty on older agents.
    /// </summary>
    public IReadOnlyList<RemovableDrive> RemovableDrives { get; init; } = Array.Empty<RemovableDrive>();

    /// <summary>
    /// Blocked sites the agent closed in the last two minutes (host + time), for the teacher's
    /// activity log; the teacher app keeps each event once. Empty on older agents.
    /// </summary>
    public IReadOnlyList<BlockedSiteEvent> RecentBlockedSites { get; init; } = Array.Empty<BlockedSiteEvent>();
}

/// <summary>
/// Bounds on what a student PC may report. The server applies them before forwarding telemetry and
/// the teacher app again before using it, so a misbehaving or tampered agent can't flood the
/// teacher's screen and activity log or put arbitrary text in them.
/// </summary>
public static class TelemetryLimits
{
    public const int MaxProcesses = 30;
    public const int MaxNameLength = 100;
    public const int MaxTitleLength = 300;
    public const int MaxLabelLength = 64;

    /// <summary>Blocked-site events older than this (or from the future) are dropped.</summary>
    public static readonly TimeSpan BlockedSiteWindow = TimeSpan.FromMinutes(5);

    public static TelemetrySnapshot Clean(TelemetrySnapshot t, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return t with
        {
            MachineId = Cap(t.MachineId, MaxNameLength) ?? string.Empty,
            System = t.System ?? new CpuMemorySample(),
            Bandwidth = t.Bandwidth ?? new BandwidthSample(),
            TopProcesses = (t.TopProcesses ?? [])
                .Where(p => p is not null)
                .Take(MaxProcesses)
                .Select(p => p with { Name = Cap(p.Name, MaxNameLength) ?? string.Empty })
                .ToArray(),
            ForegroundWindowTitle = Cap(t.ForegroundWindowTitle, MaxTitleLength),
            ActiveUrl = ClassroomLimits.CleanHost(t.ActiveUrl),
            RemovableDrives = (t.RemovableDrives ?? [])
                .Where(d => d is not null && IsDriveLetter(d.Letter))
                .Take(RemovableDrive.MaxReported)
                .Select(d => d with { Label = Cap(d.Label, MaxLabelLength), SizeBytes = Math.Max(0, d.SizeBytes) })
                .ToArray(),
            RecentBlockedSites = (t.RecentBlockedSites ?? [])
                .Where(b => b is not null && b.At >= at - BlockedSiteWindow && b.At <= at + TimeSpan.FromMinutes(1))
                .Select(b => (Event: b, Host: ClassroomLimits.CleanHost(b.Host)))
                .Where(x => x.Host is not null)
                .Take(BlockedSiteEvent.MaxReported)
                .Select(x => x.Event with { Host = x.Host! })
                .ToArray(),
        };
    }

    private static bool IsDriveLetter(string? letter) =>
        letter is { Length: 2 } && char.IsAsciiLetter(letter[0]) && letter[1] == ':';

    private static string? Cap(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > max ? clean[..max] : clean;
    }
}

/// <summary>A blocked site whose tab the agent closed.</summary>
public sealed record BlockedSiteEvent
{
    public const int MaxReported = 10;
    public string Host { get; init; } = string.Empty;
    public DateTimeOffset At { get; init; }
}

/// <summary>A flash drive or memory card on a student PC.</summary>
public sealed record RemovableDrive
{
    public const int MaxReported = 8;

    /// <summary>Drive letter, e.g. "E:".</summary>
    public string Letter { get; init; } = string.Empty;

    /// <summary>The volume's name, e.g. "KINGSTON", if it has one.</summary>
    public string? Label { get; init; }

    public long SizeBytes { get; init; }
}

public sealed record CpuMemorySample
{
    /// <summary>Total CPU utilisation across all cores, 0–100.</summary>
    public double CpuPercent { get; init; }

    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }

    public double MemoryPercent =>
        MemoryTotalBytes > 0 ? MemoryUsedBytes * 100.0 / MemoryTotalBytes : 0;
}

/// <summary>
/// Instantaneous network throughput, measured as the delta since the previous
/// sample divided by the elapsed interval — i.e. live bandwidth, not a counter.
/// </summary>
public sealed record BandwidthSample
{
    public double DownloadBytesPerSec { get; init; }
    public double UploadBytesPerSec { get; init; }

    /// <summary>Cumulative bytes since the agent started, for session totals.</summary>
    public long TotalDownloadBytes { get; init; }
    public long TotalUploadBytes { get; init; }
}

public sealed record ProcessSample
{
    public int Pid { get; init; }
    public string Name { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public long MemoryBytes { get; init; }
}
