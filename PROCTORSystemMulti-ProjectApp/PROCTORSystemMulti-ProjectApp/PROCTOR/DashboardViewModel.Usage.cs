using System.Globalization;
using System.Text;
using PROCTOR.Shared.Contracts;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Usage report: from each telemetry sample, how long every student spent in each program and
/// website, how long the PC sat idle, and how many blocked sites or programs they tried. Counted on
/// this device from when the report was started (the app opening, or "Start over").
/// </summary>
public sealed partial class DashboardViewModel
{
    // Samples arrive about once a second; a longer gap (a reconnect, a hiccup) counts as at most this.
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, UsageRecord> _usage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _usageLastSample = new(StringComparer.Ordinal);

    /// <summary>When the current report began.</summary>
    public DateTimeOffset UsageSince { get; private set; } = DateTimeOffset.Now;

    public IReadOnlyCollection<UsageRecord> Usage => _usage.Values;

    public void ResetUsage()
    {
        _usage.Clear();
        _usageLastSample.Clear();
        UsageSince = DateTimeOffset.Now;
    }

    private UsageRecord UsageFor(StudentTile tile)
    {
        if (!_usage.TryGetValue(tile.SeatKey, out var r))
            _usage[tile.SeatKey] = r = new UsageRecord { Computer = tile.MachineName };
        if (!string.IsNullOrEmpty(tile.UserName)) r.User = tile.UserName;
        return r;
    }

    /// <summary>Count the time since this student's previous sample (UI thread).</summary>
    private void TrackUsage(StudentTile tile, TelemetrySnapshot t)
    {
        var now = DateTimeOffset.Now;   // the teacher's clock: student clocks may be wrong
        var seconds = _usageLastSample.TryGetValue(tile.ConnectionId, out var last)
            ? Math.Clamp((now - last).TotalSeconds, 0, MaxSampleGap.TotalSeconds)
            : 0;
        _usageLastSample[tile.ConnectionId] = now;
        if (_usageLastSample.Count > 2000) _usageLastSample.Clear();

        var r = UsageFor(tile);
        r.Online += seconds;
        if (seconds <= 0) return;

        if (t.IdleSeconds is >= 60)
        {
            r.Idle += seconds;
            return;
        }
        r.Active += seconds;
        var app = AppLabel(t.ForegroundApp, t.ForegroundWindowTitle);
        if (app is not null) r.Apps[app] = r.Apps.GetValueOrDefault(app) + seconds;
        if (!string.IsNullOrWhiteSpace(t.ActiveUrl)) r.Sites[t.ActiveUrl] = r.Sites.GetValueOrDefault(t.ActiveUrl) + seconds;
        if (r.Apps.Count > 300) Trim(r.Apps);
        if (r.Sites.Count > 300) Trim(r.Sites);
    }

    private void CountBlockedAttempt(StudentTile tile) => UsageFor(tile).Blocked++;

    private static void Trim(Dictionary<string, double> d)
    {
        foreach (var k in d.OrderBy(kv => kv.Value).Take(d.Count - 200).Select(kv => kv.Key).ToList()) d.Remove(k);
    }

    /// <summary>
    /// "Word" for winword. Store apps (Calculator, Photos…) all run inside ApplicationFrameHost, so
    /// for those the window title says which one. Older agents send no program name: the end of the
    /// window title ("Report.docx - Word" → "Word") is the best guess.
    /// </summary>
    private static string? AppLabel(string? process, string? title)
    {
        if (!string.IsNullOrWhiteSpace(process) && !process.Equals("applicationframehost", StringComparison.OrdinalIgnoreCase))
            return FriendlyApp(process);
        if (string.IsNullOrWhiteSpace(title)) return process is null ? null : "Windows apps";
        var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
        var name = (cut >= 0 ? title[(cut + 3)..] : title).Trim();
        return name.Length == 0 ? null : name.Length > 40 ? name[..40] : name;
    }

    private static readonly Dictionary<string, string> FriendlyNames = BuildFriendlyNames();

    private static Dictionary<string, string> BuildFriendlyNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, exe) in ClassroomLimits.ProgramAliases)
        {
            var process = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
            names.TryAdd(process, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(alias));
        }
        names["msedge"] = "Microsoft Edge";
        names["code"] = "Visual Studio Code";
        names["devenv"] = "Visual Studio";
        names["explorer"] = "File Explorer";
        return names;
    }

    /// <summary>A program's everyday name: "winword" → "Word"; unknown names are returned as they are.</summary>
    public static string FriendlyApp(string process) =>
        FriendlyNames.TryGetValue(process, out var name) ? name : process;

    /// <summary>Save the report as CSV: one row per student and program / website / total.</summary>
    public string ExportUsageCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Usage report from {UsageSince:yyyy-MM-dd HH:mm} to {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("Computer,User,What,Name,Minutes");
        static string M(double seconds) => (seconds / 60).ToString("0.0", CultureInfo.InvariantCulture);
        foreach (var r in _usage.Values.OrderBy(r => r.Computer, NaturalComparer.Instance))
        {
            var who = $"{Csv(r.Computer)},{Csv(r.User ?? "")}";
            sb.AppendLine($"{who},Total,Online,{M(r.Online)}");
            sb.AppendLine($"{who},Total,Active,{M(r.Active)}");
            sb.AppendLine($"{who},Total,Idle,{M(r.Idle)}");
            sb.AppendLine($"{who},Total,Blocked attempts,{r.Blocked}");
            foreach (var (name, s) in r.Apps.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"{who},Program,{Csv(name)},{M(s)}");
            foreach (var (name, s) in r.Sites.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"{who},Website,{Csv(name)},{M(s)}");
        }
        var room = _client.CurrentServer?.RoomName ?? "Classroom";
        return TeacherFiles.Save("Usage", $"Usage_{Safe(room)}_{DateTime.Now:yyyy-MM-dd_HHmm}.csv",
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
    }
}

/// <summary>One student's usage since the report began (seconds).</summary>
public sealed class UsageRecord
{
    public required string Computer { get; init; }
    public string? User { get; set; }
    public double Online { get; set; }
    public double Active { get; set; }
    public double Idle { get; set; }
    public int Blocked { get; set; }
    public Dictionary<string, double> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Sites { get; } = new(StringComparer.OrdinalIgnoreCase);
}
