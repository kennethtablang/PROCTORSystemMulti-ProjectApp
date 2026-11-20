using System.Text.Json;
using PROCTOR.Shared.Contracts;
using Microsoft.Maui.Dispatching;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Class schedule: rules by weekday and time ("Mon, Wed 8:00–9:30 · Programming 1") that turn a set
/// of limits, or an exam, on at the start and off at the end. Saved on this device per classroom
/// and run by this app while it is connected (keep it open on the teacher PC).
/// </summary>
public sealed partial class DashboardViewModel
{
    private const string PrefScheduleActive = "proctor.schedule.active";
    private const string PrefScheduleBefore = "proctor.schedule.before";
    public const int MaxScheduleEntries = 60;

    private IDispatcherTimer? _scheduleTimer;
    private ScheduleEntry? _scheduleActive;
    private (string Id, DateOnly Day)? _scheduleSkipped;
    private bool _scheduleBusy;

    /// <summary>This classroom's schedule (empty when not connected).</summary>
    public IReadOnlyList<ScheduleEntry> Schedule => _client.CurrentServer?.ServerId is { Length: > 0 } server ? LoadSchedule(server) : [];

    /// <summary>The rule running now, if any.</summary>
    public ScheduleEntry? ActiveSchedule => _scheduleActive;

    public string ScheduleText => _scheduleActive is { } a ? $"Schedule: {a.Name} until {a.EndText}" : "";
    public bool HasActiveSchedule => _scheduleActive is not null;

    public string? SaveScheduleEntry(ScheduleEntry entry)
    {
        if (_client.CurrentServer?.ServerId is not { Length: > 0 } server) return "Connect to the classroom first.";
        var e = entry.Normalized();
        if (e.Name.Length == 0) return "Give the class a name.";
        if (e.Days == 0) return "Tick at least one day.";
        if (e.EndMinute <= e.StartMinute) return "The end time must be after the start time (on the same day).";
        var list = LoadSchedule(server).Where(x => x.Id != e.Id).ToList();
        if (list.Count >= MaxScheduleEntries) return $"There are already {MaxScheduleEntries} classes in the schedule.";
        if (list.FirstOrDefault(x => x.Enabled && e.Enabled && x.Overlaps(e)) is { } clash)
            return $"It overlaps “{clash.Name}” ({clash.DaysText} {clash.StartText}–{clash.EndText}).";
        list.Add(e);
        SaveSchedule(server, list);
        Raise(nameof(Schedule));
        CheckSchedule();
        return null;
    }

    public void DeleteScheduleEntry(ScheduleEntry entry)
    {
        if (_client.CurrentServer?.ServerId is not { Length: > 0 } server) return;
        SaveSchedule(server, LoadSchedule(server).Where(x => x.Id != entry.Id).ToList());
        Raise(nameof(Schedule));
        CheckSchedule();
    }

    /// <summary>End the running rule now; it starts again on its next day.</summary>
    public void SkipActiveSchedule()
    {
        if (_scheduleActive is not { } a) return;
        _scheduleSkipped = (a.Id, DateOnly.FromDateTime(DateTime.Now));
        CheckSchedule();
    }

    /// <summary>Start or stop the minute-by-minute check to match the connection.</summary>
    private void UpdateScheduleTimer()
    {
        if (!IsConnected) { _scheduleTimer?.Stop(); return; }
        if (_scheduleTimer is null)
        {
            if (Microsoft.Maui.Controls.Application.Current?.Dispatcher is not { } d) return;
            _scheduleTimer = d.CreateTimer();
            _scheduleTimer.Interval = TimeSpan.FromSeconds(15);
            _scheduleTimer.Tick += (_, _) => CheckSchedule();
        }
        if (!_scheduleTimer.IsRunning) _scheduleTimer.Start();
        CheckSchedule();
    }

    private async void CheckSchedule()
    {
        if (_scheduleBusy || !IsConnected || _client.CurrentServer?.ServerId is not { Length: > 0 }) return;
        _scheduleBusy = true;
        try
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            var schedule = Schedule;
            var due = schedule.FirstOrDefault(e => e.Enabled && e.IsOn(now)
                && !(_scheduleSkipped is { } skip && skip.Id == e.Id && skip.Day == today));

            // A class that was still on when this app closed, and is over now: end it (put back
            // what was on before it), so its limits don't stay on.
            if (_scheduleActive is null && LoadPref(PrefScheduleActive) is { Length: > 0 } leftOver && leftOver != due?.Id)
            {
                if (schedule.FirstOrDefault(e => e.Id == leftOver) is { } stale) await EndScheduleAsync(stale);
                else { SavePref(PrefScheduleActive, ""); SavePref(PrefScheduleBefore, ""); }
            }

            if (due?.Id == _scheduleActive?.Id) return;   // nothing changed (also: nothing on)

            if (_scheduleActive is { } running) await EndScheduleAsync(running);
            if (due is not null) await StartScheduleAsync(due);
        }
        catch (Exception ex)
        {
            Status = "Schedule: " + ex.Message;
        }
        finally
        {
            _scheduleBusy = false;
        }
    }

    private async Task StartScheduleAsync(ScheduleEntry e)
    {
        // Restarted mid-class (app closed and opened again): keep what was saved before it began.
        var resumed = LoadPref(PrefScheduleActive) == e.Id;
        string? error;
        if (e.Action == ScheduleAction.Exam)
        {
            error = _policy.ExamMode ? null : await StartExamAsync(e.Exam ?? new ExamSettings { Title = e.Name });
        }
        else
        {
            if (!resumed) SavePref(PrefScheduleBefore, JsonSerializer.Serialize(_policy with { ExamMode = false, ExamTitle = null }));
            var limits = (e.Limits ?? new ClassPolicy()) with { ExamMode = _policy.ExamMode, ExamTitle = _policy.ExamTitle };
            error = await SetPolicyAsync(limits);
        }
        if (error is not null) { Status = $"Schedule: couldn't start {e.Name}: {error}"; return; }

        SavePref(PrefScheduleActive, e.Id);
        _scheduleActive = e;
        RaiseSchedule();
        if (!resumed) AddActivity(null, "Schedule", $"{e.Name} started ({e.StartText}–{e.EndText})");
        Status = $"Schedule: {e.Name} started";
    }

    private async Task EndScheduleAsync(ScheduleEntry e)
    {
        string? error;
        if (e.Action == ScheduleAction.Exam)
        {
            error = _policy.ExamMode ? await EndExamAsync() : null;
        }
        else
        {
            ClassPolicy before;
            try { before = JsonSerializer.Deserialize<ClassPolicy>(LoadPref(PrefScheduleBefore)) ?? new(); }
            catch { before = new(); }
            error = await SetPolicyAsync(before with { ExamMode = _policy.ExamMode, ExamTitle = _policy.ExamTitle });
        }
        if (error is not null) { Status = $"Schedule: couldn't end {e.Name}: {error}"; return; }

        SavePref(PrefScheduleActive, "");
        SavePref(PrefScheduleBefore, "");
        _scheduleActive = null;
        RaiseSchedule();
        AddActivity(null, "Schedule", $"{e.Name} ended");
        Status = $"Schedule: {e.Name} ended";
    }

    private void RaiseSchedule()
    {
        Raise(nameof(ActiveSchedule));
        Raise(nameof(ScheduleText));
        Raise(nameof(HasActiveSchedule));
    }

    private static List<ScheduleEntry> LoadSchedule(string server)
    {
        try
        {
            var json = LoadPref(ScheduleKey(server));
            if (string.IsNullOrEmpty(json)) return [];
            return (JsonSerializer.Deserialize<List<ScheduleEntry>>(json) ?? [])
                .Where(e => e is not null && !string.IsNullOrEmpty(e.Id))
                .Select(e => e.Normalized())
                .OrderBy(e => e.FirstDay).ThenBy(e => e.StartMinute)
                .Take(MaxScheduleEntries)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void SaveSchedule(string server, List<ScheduleEntry> entries)
    {
        try { SavePref(ScheduleKey(server), JsonSerializer.Serialize(entries)); } catch { }
    }

    private static string ScheduleKey(string server) => "proctor.schedule." + server;
}

public enum ScheduleAction { Limits, Exam }

/// <summary>
/// One class in the schedule. <see cref="Days"/>: bit 0 = Sunday … bit 6 = Saturday.
/// Times are minutes after midnight on the teacher PC's clock.
/// </summary>
public sealed record ScheduleEntry
{
    public static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public int Days { get; init; }
    public int StartMinute { get; init; } = 8 * 60;
    public int EndMinute { get; init; } = 9 * 60 + 30;
    public bool Enabled { get; init; } = true;
    public ScheduleAction Action { get; init; }

    /// <summary>The limits to turn on (Action = Limits).</summary>
    public ClassPolicy? Limits { get; init; }

    /// <summary>The exam to run (Action = Exam).</summary>
    public ExamSettings? Exam { get; init; }

    public bool OnDay(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

    public bool IsOn(DateTime now)
    {
        var minute = now.Hour * 60 + now.Minute;
        return OnDay(now.DayOfWeek) && minute >= StartMinute && minute < EndMinute;
    }

    public bool Overlaps(ScheduleEntry other) =>
        (Days & other.Days) != 0 && StartMinute < other.EndMinute && other.StartMinute < EndMinute;

    public int FirstDay => Enumerable.Range(0, 7).Select(i => (i + 1) % 7).FirstOrDefault(d => (Days & (1 << d)) != 0, 7);

    public string DaysText => Days == 0x7F ? "Every day" : Days == 0x3E ? "Mon–Fri"
        : string.Join(", ", Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Where(d => (Days & (1 << d)) != 0).Select(d => DayNames[d]));
    public string StartText => Clock(StartMinute);
    public string EndText => Clock(EndMinute);
    public string ActionText => Action == ScheduleAction.Exam
        ? "Exam" + (Exam?.Title is { Length: > 0 } t ? ": " + t : "")
        : "Limits: " + Describe(Limits);

    private static string Clock(int minute) => new DateTime(2000, 1, 1).AddMinutes(minute).ToString("t");

    private static string Describe(ClassPolicy? p)
    {
        if (p is null || p.IsEmpty) return "none";
        var parts = new List<string>();
        if (p.WebMode == WebLimitMode.AllowOnlyListed) parts.Add($"only {p.WebHosts.Count} site(s)");
        else if (p.HasWebLimits) parts.Add($"{p.WebHosts.Count} site(s) blocked");
        if (p.AllowOnlyApps) parts.Add($"only {p.AllowedApps.Count} program(s)");
        if (p.BlockedApps.Count > 0) parts.Add($"{p.BlockedApps.Count} program(s) blocked");
        if (p.BlockInternet) parts.Add("no internet");
        if (p.BlockUsbStorage) parts.Add("no flash drives");
        if (p.MuteSound) parts.Add("muted");
        return string.Join(", ", parts);
    }

    public ScheduleEntry Normalized()
    {
        var name = new string((Name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return this with
        {
            Name = name.Length > 60 ? name[..60] : name,
            Days = Days & 0x7F,
            StartMinute = Math.Clamp(StartMinute, 0, 24 * 60 - 1),
            EndMinute = Math.Clamp(EndMinute, 1, 24 * 60),
            Action = Enum.IsDefined(Action) ? Action : ScheduleAction.Limits,
            Limits = Limits is null ? null : ClassroomLimits.Clean(Limits) with { ExamMode = false, ExamTitle = null },
        };
    }
}
