using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.Dispatching;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Screen history: every few seconds a small picture of each student's screen is saved on this
/// device (during exams, or always), so the teacher can scrub back through what a student was
/// doing. Only pictures that changed are saved, and old days are deleted automatically.
/// </summary>
public sealed partial class DashboardViewModel
{
    private const string PrefHistory = "proctor.history";

    private HistorySettings _historySettings = LoadHistorySettings();
    private IDispatcherTimer? _historyTimer;
    private readonly Dictionary<string, long> _historyVersions = new(StringComparer.Ordinal);
    private int _historyBusy;
    private DateTime _historyCleanedUtc = DateTime.MinValue;

    public HistorySettings HistorySettings
    {
        get => _historySettings;
        set
        {
            _historySettings = value.Normalized();
            try { SavePref(PrefHistory, JsonSerializer.Serialize(_historySettings)); } catch { }
            Raise();
            _historyCleanedUtc = DateTime.MinValue;   // a shorter "keep" applies now
            UpdateRecorder();
        }
    }

    /// <summary>Pictures are being saved right now.</summary>
    public bool IsRecordingHistory => IsConnected && _historySettings.Mode switch
    {
        HistoryMode.Always => true,
        HistoryMode.DuringExams => IsExamRunning,
        _ => false,
    };

    public string RecordingText => IsRecordingHistory ? $"Recording screens (every {_historySettings.IntervalSeconds} s)" : "";

    /// <summary>Start or stop the recorder to match the setting, the connection and the exam.</summary>
    private void UpdateRecorder()
    {
        var on = IsRecordingHistory;
        Streams.KeepThumbnails = on;   // keep pictures coming while the focus view or a page is open
        Raise(nameof(IsRecordingHistory));
        Raise(nameof(RecordingText));

        if (!on)
        {
            _historyTimer?.Stop();
            return;
        }
        if (_historyTimer is null)
        {
            if (Microsoft.Maui.Controls.Application.Current?.Dispatcher is not { } dispatcher) return;
            _historyTimer = dispatcher.CreateTimer();
            _historyTimer.Tick += (_, _) => CaptureHistory();
        }
        _historyTimer.Interval = TimeSpan.FromSeconds(_historySettings.IntervalSeconds);
        if (!_historyTimer.IsRunning) _historyTimer.Start();
    }

    private void CaptureHistory()
    {
        if (!IsRecordingHistory) { UpdateRecorder(); return; }
        if (Interlocked.Exchange(ref _historyBusy, 1) == 1) return;   // the last round is still saving

        var room = _client.CurrentServer?.RoomName ?? "Classroom";
        var tiles = Students.Select(t => (t.ConnectionId, t.MachineName, t.Frame)).ToArray();
        var settings = _historySettings;
        var cleanup = DateTime.UtcNow - _historyCleanedUtc > TimeSpan.FromHours(1);
        if (cleanup) _historyCleanedUtc = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            try
            {
                var now = DateTime.Now;
                var day = Path.Combine(ScreenHistory.Root, ScreenHistory.Safe(room), now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                foreach (var (id, machine, frame) in tiles)
                {
                    // Skip a screen that hasn't changed since the last picture (or has no picture yet).
                    var version = frame.Version;
                    if (version == 0) continue;
                    lock (_historyVersions)
                    {
                        if (_historyVersions.TryGetValue(id, out var last) && last == version) continue;
                    }
                    if (frame.EncodeJpeg(settings.PictureWidth, 60, out var made) is not { } jpeg) continue;
                    lock (_historyVersions) _historyVersions[id] = made;
                    var dir = Path.Combine(day, ScreenHistory.Safe(machine));
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, now.ToString("HH-mm-ss", CultureInfo.InvariantCulture) + ".jpg"), jpeg);
                }
                if (cleanup) ScreenHistory.Clean(settings.KeepDays, ScreenHistory.MaxBytes);
            }
            catch
            {
                // Disk full or folder not writable: try again next round.
            }
            finally
            {
                Interlocked.Exchange(ref _historyBusy, 0);
            }
        });
    }

    private static HistorySettings LoadHistorySettings()
    {
        try
        {
            var json = LoadPref(PrefHistory);
            return string.IsNullOrEmpty(json) ? new HistorySettings() : (JsonSerializer.Deserialize<HistorySettings>(json) ?? new()).Normalized();
        }
        catch
        {
            return new HistorySettings();
        }
    }
}

public enum HistoryMode { Off, DuringExams, Always }

/// <summary>Screen history settings (remembered on this device).</summary>
public sealed record HistorySettings
{
    public static readonly int[] Intervals = [5, 10, 15, 30, 60];
    public static readonly int[] KeepChoices = [1, 3, 7, 14, 30];

    public HistoryMode Mode { get; init; } = HistoryMode.DuringExams;
    public int IntervalSeconds { get; init; } = 10;
    public int KeepDays { get; init; } = 7;
    /// <summary>Picture width; the thumbnail stream is usually smaller, and is never scaled up.</summary>
    public int PictureWidth { get; init; } = 800;

    public HistorySettings Normalized() => this with
    {
        IntervalSeconds = Intervals.Contains(IntervalSeconds) ? IntervalSeconds : 10,
        KeepDays = Math.Clamp(KeepDays, 1, 365),
        PictureWidth = Math.Clamp(PictureWidth, 320, 1920),
    };
}

/// <summary>
/// Where screen history is kept: Documents\PROCTOR\Screen history\{room}\{yyyy-MM-dd}\{computer}\{HH-mm-ss}.jpg
/// (inside the app on phones and tablets).
/// </summary>
public static class ScreenHistory
{
    /// <summary>History never grows past this (5 GB on a PC, 1 GB on a phone or tablet); the oldest days go first.</summary>
    public static long MaxBytes => (OperatingSystem.IsWindows() ? 5L : 1L) * 1024 * 1024 * 1024;

    public static string Root => Path.Combine(TeacherFiles.Root, "Screen history");

    public static string Safe(string name)
    {
        var clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "_" : clean;
    }

    /// <summary>Recorded days, newest first.</summary>
    public static IReadOnlyList<HistoryDay> Days()
    {
        var days = new List<HistoryDay>();
        try
        {
            if (!Directory.Exists(Root)) return days;
            foreach (var room in Directory.GetDirectories(Root))
                foreach (var dir in Directory.GetDirectories(room))
                    if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        days.Add(new HistoryDay(Path.GetFileName(room), date, dir));
        }
        catch { }
        return days.OrderByDescending(d => d.Date).ThenBy(d => d.Room, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Computers recorded on a day, in name order.</summary>
    public static IReadOnlyList<string> Computers(HistoryDay day)
    {
        try
        {
            return Directory.GetDirectories(day.Folder).Select(Path.GetFileName).OfType<string>()
                .OrderBy(n => n, NaturalComparer.Instance).ToList();
        }
        catch { return []; }
    }

    /// <summary>A computer's pictures that day, oldest first.</summary>
    public static IReadOnlyList<HistoryShot> Shots(HistoryDay day, string computer)
    {
        try
        {
            var dir = Path.Combine(day.Folder, computer);
            if (!Directory.Exists(dir)) return [];
            return Directory.GetFiles(dir, "*.jpg")
                .Select(f => (File: f, Ok: TimeSpan.TryParseExact(Path.GetFileNameWithoutExtension(f), @"hh\-mm\-ss", CultureInfo.InvariantCulture, out var t), Time: t))
                .Where(x => x.Ok)
                .OrderBy(x => x.Time)
                .Select(x => new HistoryShot(x.File, day.Date + x.Time))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>Delete days older than <paramref name="keepDays"/>, then the oldest days while over <paramref name="maxBytes"/>.</summary>
    public static void Clean(int keepDays, long maxBytes)
    {
        var days = Days().OrderBy(d => d.Date).ToList();
        var cutoff = DateTime.Today.AddDays(-(keepDays - 1));
        foreach (var d in days.Where(d => d.Date < cutoff).ToList())
        {
            Delete(d);
            days.Remove(d);
        }

        var sizes = days.ToDictionary(d => d.Folder, Size);
        var total = sizes.Values.Sum();
        foreach (var d in days)
        {
            if (total <= maxBytes || d.Date == DateTime.Today) break;   // never delete the day being recorded
            total -= sizes[d.Folder];
            Delete(d);
        }
    }

    public static void Delete(HistoryDay day)
    {
        try
        {
            Directory.Delete(day.Folder, recursive: true);
            var room = Path.GetDirectoryName(day.Folder);
            if (room is not null && Directory.Exists(room) && !Directory.EnumerateFileSystemEntries(room).Any()) Directory.Delete(room);
        }
        catch { /* in use: next clean-up */ }
    }

    private static long Size(HistoryDay day)
    {
        try { return Directory.EnumerateFiles(day.Folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}

public sealed record HistoryDay(string Room, DateTime Date, string Folder)
{
    public string Title => $"{Date:ddd d MMM yyyy} · {Room}";
}

public sealed record HistoryShot(string File, DateTime At);
