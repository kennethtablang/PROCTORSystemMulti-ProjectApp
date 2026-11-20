using PROCTOR.Shared.Contracts;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Attendance by name: the teacher asks the class, every student PC shows a form (first name, last
/// name, middle initial), and each name appears on that student's screen in the dashboard and in
/// the attendance export. "New class" clears the names so the next class can give theirs.
/// </summary>
public sealed partial class DashboardViewModel
{
    private AttendanceStatus _attendanceStatus = new();

    /// <summary>The server's attendance state: whether students are being asked right now.</summary>
    public AttendanceStatus AttendanceStatus
    {
        get => _attendanceStatus;
        private set { _attendanceStatus = value; RaiseAttendance(); }
    }

    public bool IsTakingAttendance => _attendanceStatus.Open is not null;
    public int NamedCount => Students.Count(s => s.HasStudentName);

    public string AttendanceText
    {
        get
        {
            if (_attendanceStatus.Open is not { } open) return "";
            var title = open.Title is { Length: > 0 } t ? $" · {t}" : "";
            return $"Taking attendance{title}: {NamedCount} of {Students.Count} student PC(s) have given a name";
        }
    }

    private void RaiseAttendance()
    {
        Raise(nameof(AttendanceStatus));
        Raise(nameof(IsTakingAttendance));
        Raise(nameof(NamedCount));
        Raise(nameof(AttendanceText));
    }

    private void OnStudentName(StudentNameUpdate u)
    {
        if (!_byConnection.TryGetValue(u.StudentConnectionId, out var tile)) return;
        tile.StudentName = u.Name;
        if (_attendanceByConnection.TryGetValue(u.StudentConnectionId, out var key) && _attendance.TryGetValue(key, out var row))
            row.Name = u.Name;
        if (u.Name is { } n) AddActivity(tile, "Attendance", $"{n.FullName}");
        RaiseAttendance();
        RefreshVisible();   // names are searchable
    }

    /// <summary>Ask the class for their names (optionally with the class's name shown on the form).</summary>
    public async Task<string?> StartAttendanceAsync(string? title)
    {
        try { await _client.StartAttendanceAsync(AttendanceLimits.CleanTitle(title)); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task<string?> StopAttendanceAsync()
    {
        try { await _client.StopAttendanceAsync(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// A new class: the names are forgotten (on the server and in this app), the attendance list and
    /// the usage report start again with the PCs connected now, and the students are optionally asked
    /// for their names straight away.
    /// </summary>
    public async Task<string?> NewClassAsync(bool askAgain)
    {
        try { await _client.ClearAttendanceAsync(askAgain); }
        catch (Exception ex) { return ex.Message; }

        var now = DateTimeOffset.Now;
        foreach (var key in _attendance.Where(kv => !kv.Value.Online).Select(kv => kv.Key).ToList()) _attendance.Remove(key);
        foreach (var row in _attendance.Values)
        {
            row.FirstSeen = now;
            row.LastSeen = now;
            row.Name = null;
        }
        foreach (var t in Students) t.StudentName = null;
        ResetUsage();
        AddActivity(null, "Attendance", askAgain ? "New class: names cleared, students asked again" : "New class: names cleared");
        RaiseAttendance();
        RefreshVisible();
        return null;
    }
}
