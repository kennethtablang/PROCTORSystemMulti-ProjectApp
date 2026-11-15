using System.Globalization;
using System.Text;
using ClassInsight.Shared.Contracts;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// Teacher tools ("quick wins"): lesson timer, random student picker, attendance export and
/// screenshots. Attendance and screenshots are kept on the teacher's own device only — the server
/// still stores nothing (AD-6).
/// </summary>
public sealed partial class DashboardViewModel
{
    // ---- lesson timer -------------------------------------------------------

    private LessonTimer _timer = new();
    private DateTime _timerEndsUtc;
    private IDispatcherTimer? _timerTick;

    public bool TimerRunning => _timer.Running && _timerEndsUtc > DateTime.UtcNow;

    public string TimerText
    {
        get
        {
            if (!TimerRunning) return "";
            var secs = (int)Math.Ceiling((_timerEndsUtc - DateTime.UtcNow).TotalSeconds);
            var clock = secs >= 3600 ? $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}" : $"{secs / 60}:{secs % 60:00}";
            return string.IsNullOrEmpty(_timer.Label) ? $"⏱ {clock} left" : $"⏱ {clock} left · {_timer.Label}";
        }
    }

    private void OnTimerUpdated(LessonTimer t)
    {
        _timer = t;
        _timerEndsUtc = DateTime.UtcNow.AddSeconds(t.RemainingSeconds);
        if (t.Running && _timerTick is null && Application.Current?.Dispatcher is { } d)
        {
            _timerTick = d.CreateTimer();
            _timerTick.Interval = TimeSpan.FromSeconds(1);
            _timerTick.Tick += (_, _) =>
            {
                Raise(nameof(TimerText));
                if (!TimerRunning) { _timerTick?.Stop(); _timerTick = null; Raise(nameof(TimerRunning)); }
            };
            _timerTick.Start();
        }
        Raise(nameof(TimerRunning));
        Raise(nameof(TimerText));
    }

    public async Task<string?> StartTimerAsync(int seconds, string? label)
    {
        try { await _client.StartTimerAsync(new TimerRequest { Seconds = seconds, Label = label }); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task<string?> StopTimerAsync()
    {
        try { await _client.StopTimerAsync(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    // ---- random student picker -------------------------------------------

    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);

    /// <summary>
    /// Pick a student at random, avoiding anyone already picked this session until everyone has had
    /// a turn. Returns null when no students are connected.
    /// </summary>
    public StudentTile? PickRandomStudent()
    {
        if (Students.Count == 0) return null;
        var pool = Students.Where(s => !_picked.Contains(s.MachineName)).ToList();
        if (pool.Count == 0) { _picked.Clear(); pool = Students.ToList(); }
        var pick = pool[Random.Shared.Next(pool.Count)];
        _picked.Add(pick.MachineName);
        return pick;
    }

    // ---- attendance ---------------------------------------------------------

    private sealed class AttendanceRow
    {
        public required string Machine { get; init; }
        public string? User { get; set; }
        public string Ip { get; set; } = "";
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public bool Online { get; set; }
    }

    // Keyed by machine id so reconnects don't create duplicate rows.
    private readonly Dictionary<string, AttendanceRow> _attendance = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _attendanceByConnection = new(StringComparer.Ordinal);

    private void TrackJoin(StudentSnapshot s)
    {
        var key = string.IsNullOrEmpty(s.MachineId) ? s.MachineName : s.MachineId;
        var now = DateTimeOffset.Now;
        if (!_attendance.TryGetValue(key, out var row))
            _attendance[key] = row = new AttendanceRow { Machine = s.MachineName, FirstSeen = now };
        row.User = s.UserName ?? row.User;
        row.Ip = s.IpAddress;
        row.LastSeen = now;
        row.Online = true;
        _attendanceByConnection[s.ConnectionId] = key;
    }

    private void TrackLeave(string connectionId)
    {
        if (_attendanceByConnection.Remove(connectionId, out var key) && _attendance.TryGetValue(key, out var row))
        {
            row.LastSeen = DateTimeOffset.Now;
            row.Online = false;
        }
    }

    /// <summary>Write this session's attendance as CSV on this device; returns the file path.</summary>
    public string ExportAttendanceCsv()
    {
        var now = DateTimeOffset.Now;
        var sb = new StringBuilder();
        sb.AppendLine("Computer,User,IP address,First seen,Last seen,Minutes online,Online now");
        foreach (var r in _attendance.Values.OrderBy(r => r.Machine, StringComparer.OrdinalIgnoreCase))
        {
            var last = r.Online ? now : r.LastSeen;
            var minutes = Math.Max(0, (int)Math.Round((last - r.FirstSeen).TotalMinutes));
            sb.AppendLine(string.Join(",",
                Csv(r.Machine), Csv(r.User ?? ""), Csv(r.Ip),
                Csv(r.FirstSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Csv(last.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                minutes.ToString(CultureInfo.InvariantCulture),
                r.Online ? "yes" : "no"));
        }
        var room = _client.CurrentServer?.RoomName ?? "Classroom";
        return TeacherFiles.Save("Attendance", $"Attendance_{Safe(room)}_{now:yyyy-MM-dd_HHmm}.csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
    }

    public int AttendanceCount => _attendance.Count;

    // CSV cell: quote when needed; neutralise leading =,+,-,@ so a spreadsheet can't run it as a formula.
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // ---- screenshots ------------------------------------------------------

    /// <summary>Save one student's current frame as PNG; returns the path, or null if there's no picture yet.</summary>
    public static string? SaveScreenshot(StudentTile tile)
    {
        var png = tile.Frame.EncodePng();
        return png is null ? null : TeacherFiles.Save("Screenshots", $"{Safe(tile.MachineName)}_{DateTime.Now:yyyy-MM-dd_HHmmss}.png", png);
    }

    /// <summary>Save every student's current thumbnail into one timestamped folder; returns (folder, count).</summary>
    public (string? Folder, int Count) SaveAllThumbnails()
    {
        var folder = $"Class_{DateTime.Now:yyyy-MM-dd_HHmmss}";
        string? dir = null;
        var n = 0;
        foreach (var t in Students)
        {
            if (t.Frame.EncodePng() is not { } png) continue;
            var path = TeacherFiles.Save(Path.Combine("Screenshots", folder), $"{Safe(t.MachineName)}.png", png);
            dir = Path.GetDirectoryName(path);
            n++;
        }
        return (dir, n);
    }

    private static string Safe(string name) =>
        new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
}

/// <summary>Where the teacher app saves files (screenshots, attendance): Documents\ClassInsight on desktop.</summary>
public static class TeacherFiles
{
    public static string Root
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrEmpty(docs) ? Path.Combine(FileSystem.AppDataDirectory, "ClassInsight") : Path.Combine(docs, "ClassInsight");
        }
    }

    public static string Save(string subfolder, string fileName, byte[] bytes)
    {
        var dir = Path.Combine(Root, subfolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
