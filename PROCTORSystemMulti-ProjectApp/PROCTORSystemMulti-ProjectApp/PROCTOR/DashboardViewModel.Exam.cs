using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using PROCTOR.Shared.Contracts;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>Exam mode (one switch for test conditions) and the lesson's activity log.</summary>
public sealed partial class DashboardViewModel
{
    // ---- exam mode ----------------------------------------------------------------------------

    private const string PrefExamBefore = "proctor.exam.before";

    public bool IsExamRunning => _policy.ExamMode;

    public string ExamText => _policy.ExamMode
        ? "Exam in progress" + (string.IsNullOrEmpty(_policy.ExamTitle) ? "" : " · " + _policy.ExamTitle) + " · " + ExamDetail(_policy)
        : "";

    private static string ExamDetail(ClassPolicy p)
    {
        var parts = new List<string>();
        if (p.WebMode == WebLimitMode.AllowOnlyListed) parts.Add($"only {p.WebHosts.Count} site(s)");
        if (p.BlockInternet) parts.Add("no internet");
        if (p.BlockUsbStorage) parts.Add("flash drives blocked");
        if (p.MuteSound) parts.Add("sound muted");
        if (p.BlockedApps.Count > 0) parts.Add($"{p.BlockedApps.Count} program(s) blocked");
        if (p.AllowOnlyApps) parts.Add($"only {p.AllowedApps.Count} program(s)");
        return parts.Count == 0 ? "students see the exam bar" : string.Join(", ", parts);
    }

    /// <summary>
    /// Start an exam: the current limits plus the exam's (the stricter wins). What was set before is
    /// remembered on this device and put back by <see cref="EndExamAsync"/>.
    /// </summary>
    public async Task<string?> StartExamAsync(ExamSettings exam)
    {
        var before = _policy.ExamMode ? LoadBeforeExam() ?? _policy : _policy;
        var p = before with
        {
            ExamMode = true,
            ExamTitle = string.IsNullOrWhiteSpace(exam.Title) ? null : exam.Title.Trim(),
            BlockUsbStorage = before.BlockUsbStorage || exam.BlockUsb,
            MuteSound = before.MuteSound || exam.Mute,
            BlockedApps = before.BlockedApps.Concat(exam.BlockedApps).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        if (exam.AllowedApps.Count > 0) p = p with { AllowOnlyApps = true, AllowedApps = exam.AllowedApps };
        p = exam.Web switch
        {
            ExamWeb.OnlyListed => p with { WebMode = WebLimitMode.AllowOnlyListed, WebHosts = exam.Sites, BlockInternet = false },
            ExamWeb.NoInternet => p with { BlockInternet = true },
            _ => p,
        };

        var error = await SetPolicyAsync(p);
        if (error is null && !_policy.ExamMode) error = "The classroom server didn't start the exam.";
        if (error is null)
        {
            SaveBeforeExam(before);
            AddActivity(null, "Exam", $"Exam started{(p.ExamTitle is { } t ? ": " + t : "")}");
        }
        return error;
    }

    /// <summary>End the exam and put back the limits from before it.</summary>
    public async Task<string?> EndExamAsync()
    {
        var restore = LoadBeforeExam() ?? (_policy with { ExamMode = false, ExamTitle = null });
        var error = await SetPolicyAsync(restore with { ExamMode = false, ExamTitle = null });
        if (error is null)
        {
            SavePref(PrefExamBefore, "");
            AddActivity(null, "Exam", "Exam ended");
        }
        return error;
    }

    private void SaveBeforeExam(ClassPolicy p)
    {
        try { SavePref(PrefExamBefore, JsonSerializer.Serialize(p)); } catch { }
    }

    private static ClassPolicy? LoadBeforeExam()
    {
        try
        {
            var json = LoadPref(PrefExamBefore);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ClassPolicy>(json);
        }
        catch { return null; }
    }

    // ---- activity log -------------------------------------------------------------------------

    private const int MaxActivity = 5000;

    /// <summary>What happened this session, newest first: programs, websites, blocked sites, flash drives, joins.</summary>
    public ObservableCollection<ActivityEntry> Activity { get; } = new();

    private readonly HashSet<string> _seenBlocked = new(StringComparer.Ordinal);

    /// <summary>Add an entry (UI thread). <paramref name="tile"/> null = about the whole class.</summary>
    public void AddActivity(StudentTile? tile, string kind, string detail)
    {
        Activity.Insert(0, new ActivityEntry(DateTimeOffset.Now, tile?.MachineName ?? "Class", tile?.UserName ?? "", kind, detail));
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
    }

    /// <summary>Compare a student's telemetry with what we had, and log what changed.</summary>
    private void TrackActivity(StudentTile tile, string oldWindow, string oldHost, bool oldUsb, string oldUsbText, TelemetrySnapshot t)
    {
        if (tile.ForegroundText != oldWindow && !string.IsNullOrWhiteSpace(tile.ForegroundText))
            AddActivity(tile, "Program", tile.ForegroundText);
        if (tile.ActiveHostText != oldHost && !string.IsNullOrWhiteSpace(tile.ActiveHostText))
            AddActivity(tile, "Website", tile.ActiveHostText);
        if (tile.HasUsbDrive && !oldUsb)
        {
            var drive = tile.UsbText.Replace("Flash drive: ", "");
            AddActivity(tile, "Flash drive", "Plugged in: " + drive);
            RaiseAlert(tile, tile.MachineName, AlertKind.FlashDrive, "Plugged in: " + drive);
        }
        else if (!tile.HasUsbDrive && oldUsb) AddActivity(tile, "Flash drive", "Removed: " + oldUsbText.Replace("Flash drive: ", ""));

        foreach (var b in t.RecentBlockedSites ?? [])
        {
            if (string.IsNullOrWhiteSpace(b.Host)) continue;
            if (!_seenBlocked.Add($"{tile.ConnectionId}|{b.Host}|{b.At.UtcTicks}")) continue;
            if (_seenBlocked.Count > 20000) _seenBlocked.Clear();
            var site = b.Host.Length > 100 ? b.Host[..100] : b.Host;
            AddActivity(tile, "Blocked site", site);
            CountBlockedAttempt(tile);
            RaiseAlert(tile, tile.MachineName, AlertKind.BlockedSite, site);
        }

        foreach (var a in t.RecentBlockedApps ?? [])
        {
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            if (!_seenBlocked.Add($"{tile.ConnectionId}|app:{a.Name}|{a.At.UtcTicks}")) continue;
            if (_seenBlocked.Count > 20000) _seenBlocked.Clear();
            AddActivity(tile, "Blocked program", FriendlyApp(a.Name) + " (closed)");
            CountBlockedAttempt(tile);
            RaiseAlert(tile, tile.MachineName, AlertKind.BlockedProgram, a.Name);
        }
    }

    /// <summary>Save the activity log as CSV (Documents\PROCTOR\Activity); returns the file.</summary>
    public string ExportActivityCsv(string? student)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Time,Computer,User,What,Details");
        foreach (var e in Activity.Where(a => student is null || a.Computer == student).Reverse())
            sb.AppendLine(string.Join(",", Csv(e.At.ToString("yyyy-MM-dd HH:mm:ss")), Csv(e.Computer), Csv(e.User), Csv(e.Kind), Csv(e.Detail)));
        var room = _client.CurrentServer?.RoomName ?? "Classroom";
        var name = $"Activity_{Safe(room)}{(student is null ? "" : "_" + Safe(student))}_{DateTime.Now:yyyy-MM-dd_HHmm}.csv";
        return TeacherFiles.Save("Activity", name, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
    }
}

/// <summary>One line in the activity log.</summary>
public sealed record ActivityEntry(DateTimeOffset At, string Computer, string User, string Kind, string Detail)
{
    public string TimeText => At.ToString("HH:mm:ss");
    public string Who => string.IsNullOrEmpty(User) ? Computer : $"{Computer} · {User}";
    public Color KindColor => Kind switch
    {
        "Blocked site" or "Blocked program" => Color.FromArgb("#EF5350"),
        "Flash drive" => Color.FromArgb("#FF8A65"),
        "Website" => Color.FromArgb("#7E57C2"),
        "Joined" or "Left" => Color.FromArgb("#26A69A"),
        "Exam" => Color.FromArgb("#F2C230"),
        _ => Color.FromArgb("#9AA0A6"),
    };
}

public enum ExamWeb { Keep, OnlyListed, NoInternet }

/// <summary>What an exam turns on (remembered on this device for next time).</summary>
public sealed record ExamSettings
{
    public string? Title { get; init; }
    public ExamWeb Web { get; init; } = ExamWeb.Keep;
    public IReadOnlyList<string> Sites { get; init; } = [];
    public bool BlockUsb { get; init; } = true;
    public bool Mute { get; init; } = true;
    public IReadOnlyList<string> BlockedApps { get; init; } = [];
    /// <summary>When not empty: only these programs may be used during the exam.</summary>
    public IReadOnlyList<string> AllowedApps { get; init; } = [];
}
