using System.Collections.ObjectModel;
using System.Text.Json;
using PROCTOR.Shared.Contracts;
using Microsoft.Maui.Devices;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Teacher alerts: a student opened a blocked site or program, plugged in a flash drive, has a
/// watched word in a window title or website, hasn't touched the PC for a while, or left during an
/// exam. Each alert turns that student's outline red, shows a banner with a sound (Windows) or a
/// buzz (phones), and is listed until the teacher dismisses it. Kept on this device only.
/// </summary>
public sealed partial class DashboardViewModel
{
    private const string PrefAlerts = "proctor.alerts";
    private const int MaxAlerts = 500;
    private static readonly TimeSpan AlertRepeat = TimeSpan.FromMinutes(1);

    private AlertSettings _alertSettings = LoadAlertSettings();
    private readonly Dictionary<string, DateTimeOffset> _lastAlert = new(StringComparer.Ordinal);

    /// <summary>Every alert this session, newest first.</summary>
    public ObservableCollection<AlertEntry> Alerts { get; } = new();

    public AlertSettings AlertSettings
    {
        get => _alertSettings;
        set
        {
            _alertSettings = value.Normalized();
            try { SavePref(PrefAlerts, JsonSerializer.Serialize(_alertSettings)); } catch { }
            Raise();
        }
    }

    private int _newAlerts;
    /// <summary>Alerts the teacher hasn't dismissed or looked at yet.</summary>
    public int NewAlerts
    {
        get => _newAlerts;
        private set
        {
            if (_newAlerts == value) return;
            _newAlerts = value;
            Raise(); Raise(nameof(HasNewAlerts)); Raise(nameof(AlertBannerText));
        }
    }
    public bool HasNewAlerts => _newAlerts > 0;

    public string AlertBannerText
    {
        get
        {
            if (_newAlerts == 0 || Alerts.Count == 0) return "";
            var last = Alerts[0];
            var latest = $"{last.Computer}: {last.Kind} · {last.Detail}";
            return _newAlerts == 1 ? latest : $"{_newAlerts} new alerts · latest: {latest}";
        }
    }

    /// <summary>Raise an alert (UI thread) unless that kind is switched off or it just fired.</summary>
    private void RaiseAlert(StudentTile? tile, string computer, AlertKind kind, string detail)
    {
        var s = _alertSettings;
        if (!s.Enabled || !s.IsOn(kind)) return;

        var now = DateTimeOffset.Now;
        var key = $"{computer}|{kind}|{detail}";
        if (_lastAlert.TryGetValue(key, out var last) && now - last < AlertRepeat) return;
        _lastAlert[key] = now;
        if (_lastAlert.Count > 5000) _lastAlert.Clear();

        var entry = new AlertEntry(now, computer, tile?.UserName ?? "", AlertEntry.Title(kind), detail);
        Alerts.Insert(0, entry);
        while (Alerts.Count > MaxAlerts) Alerts.RemoveAt(Alerts.Count - 1);
        if (tile is not null) tile.AlertText = $"{entry.Kind}: {detail}";
        _newAlerts++;
        Raise(nameof(NewAlerts)); Raise(nameof(HasNewAlerts)); Raise(nameof(AlertBannerText));
        Status = $"Alert · {computer}: {entry.Kind} · {detail}";
        if (s.Sound) AlertSound.Play();
    }

    /// <summary>Clear the banner and every red outline (the list stays).</summary>
    public void DismissAlerts()
    {
        foreach (var t in Students) t.AlertText = null;
        NewAlerts = 0;
    }

    /// <summary>Clear one student's red outline.</summary>
    public void DismissAlert(StudentTile tile)
    {
        tile.AlertText = null;
        if (!Students.Any(s => s.HasAlert)) NewAlerts = 0;
    }

    /// <summary>Called with each new telemetry sample, after the activity log has been updated.</summary>
    private void CheckAlerts(StudentTile tile, string oldWindow, string oldHost, int? oldIdle)
    {
        var s = _alertSettings;
        if (!s.Enabled) return;

        // Watched words: in the window title or the website, when either changes.
        if (s.Words.Count > 0 && (tile.ForegroundText != oldWindow || tile.ActiveHostText != oldHost))
        {
            foreach (var text in new[] { tile.ActiveHostText, tile.ForegroundText })
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (s.Words.FirstOrDefault(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) is not { } word) continue;
                RaiseAlert(tile, tile.MachineName, AlertKind.Word, $"“{word}” in {Short(text)}");
                break;
            }
        }

        // Idle: crossed the threshold since the last sample (fires once per idle spell).
        if (s.Idle && tile.IdleSeconds is { } idle)
        {
            var limit = s.IdleMinutes * 60;
            if (idle >= limit && (oldIdle is null || oldIdle < limit))
                RaiseAlert(tile, tile.MachineName, AlertKind.Idle, $"No keyboard or mouse for {s.IdleMinutes} min");
        }
    }

    private static string Short(string text) => text.Length > 80 ? text[..80] + "…" : text;

    private static AlertSettings LoadAlertSettings()
    {
        try
        {
            var json = LoadPref(PrefAlerts);
            return string.IsNullOrEmpty(json) ? new AlertSettings() : (JsonSerializer.Deserialize<AlertSettings>(json) ?? new()).Normalized();
        }
        catch
        {
            return new AlertSettings();
        }
    }
}

public enum AlertKind { BlockedSite, BlockedProgram, FlashDrive, Word, Idle, LeftExam }

/// <summary>What raises an alert (remembered on this device).</summary>
public sealed record AlertSettings
{
    public const int MaxWords = 50;
    public const int MaxWordLength = 40;

    public bool Enabled { get; init; } = true;
    public bool BlockedSites { get; init; } = true;
    public bool BlockedPrograms { get; init; } = true;
    public bool FlashDrives { get; init; } = true;
    public bool LeftDuringExam { get; init; } = true;
    public bool Idle { get; init; }
    public int IdleMinutes { get; init; } = 5;
    public IReadOnlyList<string> Words { get; init; } = [];
    public bool Sound { get; init; } = true;

    public bool IsOn(AlertKind kind) => kind switch
    {
        AlertKind.BlockedSite => BlockedSites,
        AlertKind.BlockedProgram => BlockedPrograms,
        AlertKind.FlashDrive => FlashDrives,
        AlertKind.Word => Words.Count > 0,
        AlertKind.Idle => Idle,
        AlertKind.LeftExam => LeftDuringExam,
        _ => false,
    };

    /// <summary>Trimmed, de-duplicated words; idle minutes within 1–120.</summary>
    public AlertSettings Normalized() => this with
    {
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 120),
        Words = ParseWords(string.Join(",", Words ?? [])),
    };

    /// <summary>"chatgpt, game; roblox" → ["chatgpt", "game", "roblox"].</summary>
    public static IReadOnlyList<string> ParseWords(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => new string(w.Where(c => !char.IsControl(c)).ToArray()).Trim())
            .Where(w => w.Length >= 2)
            .Select(w => w.Length > MaxWordLength ? w[..MaxWordLength] : w)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxWords)
            .ToArray();
}

/// <summary>One alert in the list.</summary>
public sealed record AlertEntry(DateTimeOffset At, string Computer, string User, string Kind, string Detail)
{
    public string TimeText => At.ToString("HH:mm:ss");
    public string Who => string.IsNullOrEmpty(User) ? Computer : $"{Computer} · {User}";

    public static string Title(AlertKind kind) => kind switch
    {
        AlertKind.BlockedSite => "Blocked site",
        AlertKind.BlockedProgram => "Blocked program",
        AlertKind.FlashDrive => "Flash drive",
        AlertKind.Word => "Watched word",
        AlertKind.Idle => "Idle",
        AlertKind.LeftExam => "Left the exam",
        _ => "Alert",
    };
}

/// <summary>The alert sound: two short tones on Windows, a buzz on phones and tablets.</summary>
public static class AlertSound
{
    private static DateTime _last = DateTime.MinValue;

    public static void Play()
    {
        // Several alerts at once (a whole row opening the same site) make one sound.
        if (DateTime.UtcNow - _last < TimeSpan.FromSeconds(3)) return;
        _last = DateTime.UtcNow;
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() =>
            {
                try { Console.Beep(988, 120); Console.Beep(1319, 180); }
                catch { /* no sound device */ }
            });
            return;
        }
        try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(300)); }
        catch { /* no vibration motor, or not allowed */ }
    }
}
