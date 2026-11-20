using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Attendance: when the teacher asks, a form in the middle of the screen asks for the student's
/// first name, last name and middle initial. It stays on top until they submit (or the teacher
/// stops asking), then thanks them and closes.
/// </summary>
internal sealed partial class ClassroomUi
{
    private Window? _attendanceWindow;
    private string? _attendanceId;

    /// <summary>Raised (UI thread) when the student submits; returns an error to show, or null when recorded.</summary>
    public event Func<AttendanceSubmission, Task<string?>>? AttendanceSubmitted;

    public void ShowAttendance(AttendanceRequest request) => Post(() =>
    {
        if (_attendanceWindow is not null && _attendanceId == request.Id) { _attendanceWindow.Activate(); return; }
        _attendanceWindow?.Close();
        _attendanceId = request.Id;

        var accent = new SolidColorBrush(Color.FromRgb(0xB7, 0x94, 0xF6));
        var muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));
        var body = new StackPanel { Margin = new Thickness(26, 22, 26, 22), Width = 440 };
        body.Children.Add(new TextBlock
        {
            Text = "Attendance",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
        });
        body.Children.Add(new TextBlock
        {
            Text = request.Title is { Length: > 0 } t ? t : "Your teacher is taking attendance",
            FontSize = 20,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
        });
        body.Children.Add(new TextBlock
        {
            Text = "Type your name, then press Submit.",
            FontSize = 13,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 14),
        });

        TextBox Field(string label, int maxLength, double width)
        {
            body.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 3) });
            var box = new TextBox
            {
                MaxLength = maxLength,
                FontSize = 16,
                Padding = new Thickness(6, 4, 6, 4),
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            body.Children.Add(box);
            return box;
        }

        var first = Field("First name", AttendanceLimits.MaxNameLength, 388);
        var last = Field("Last name", AttendanceLimits.MaxNameLength, 388);
        var middle = Field("Middle initial (optional)", 2, 70);

        var status = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x9A, 0x9A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var submit = new Button
        {
            Content = "Submit",
            FontSize = 15,
            Padding = new Thickness(26, 7, 26, 7),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,   // Enter submits
        };
        body.Children.Add(status);
        body.Children.Add(submit);

        var window = Card(body);
        window.Title = "PROCTOR attendance";
        submit.Click += async (_, _) =>
        {
            var submission = new AttendanceSubmission
            {
                FirstName = first.Text ?? "",
                LastName = last.Text ?? "",
                MiddleInitial = (middle.Text ?? "").Trim().TrimEnd('.'),
            };
            // Check here first so the student sees mistakes at once; the server checks again.
            if (AttendanceLimits.Clean(submission, DateTimeOffset.UtcNow, out var local) is null)
            {
                status.Text = local;
                return;
            }
            if (AttendanceSubmitted is not { } send) { status.Text = "Not connected to your teacher. Try again in a moment."; return; }

            submit.IsEnabled = false;
            status.Foreground = muted;
            status.Text = "Sending…";
            string? error;
            try { error = await send(submission); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Attendance submit failed.");
                error = "Couldn't reach your teacher. Try again.";
            }
            if (error is not null)
            {
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x9A, 0x9A));
                status.Text = error;
                submit.IsEnabled = true;
                return;
            }

            var name = AttendanceLimits.Clean(submission, DateTimeOffset.UtcNow, out _)!;
            status.Foreground = new SolidColorBrush(Color.FromRgb(0xA5, 0xD6, 0xA7));
            status.Text = $"✔ Thank you, {name.FirstName}. Your attendance was recorded.";
            first.IsEnabled = last.IsEnabled = middle.IsEnabled = false;
            var close = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            close.Tick += (_, _) => { close.Stop(); if (ReferenceEquals(_attendanceWindow, window)) CloseAttendanceNow(); };
            close.Start();
        };

        // The form can't be closed with Alt+F4: it goes away once the name is in (or the teacher stops).
        window.Closing += (_, e) => { if (ReferenceEquals(_attendanceWindow, window) && window.Tag is not "closing") e.Cancel = true; };
        window.Loaded += (_, _) => { window.Activate(); first.Focus(); Keyboard.Focus(first); };
        _attendanceWindow = window;
        window.Show();
        _logger.LogInformation("Attendance form shown.");
    });

    /// <summary>The teacher stopped asking (or the connection was lost).</summary>
    public void CloseAttendance() => Post(CloseAttendanceNow);

    private void CloseAttendanceNow()
    {
        if (_attendanceWindow is not { } w) return;
        _attendanceWindow = null;
        _attendanceId = null;
        w.Tag = "closing";
        w.Close();
    }
}
