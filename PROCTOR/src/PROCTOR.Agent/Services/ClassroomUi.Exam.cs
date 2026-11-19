using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Exam mode: an always-visible "Exam in progress" bar at the bottom of the student's screen while the
/// teacher runs an exam. Clicks pass through it, so it never gets in the way of the exam itself.
/// </summary>
internal sealed partial class ClassroomUi
{
    private Window? _examWindow;
    private TextBlock? _examText;

    public void ShowExam(bool active, string? title) => Post(() =>
    {
        if (!active)
        {
            if (_examWindow is null) return;
            _examWindow.Close();
            _examWindow = null;
            _examText = null;
            _logger.LogInformation("Exam mode ended.");
            return;
        }

        var text = "\U0001F4DD  Exam in progress" + (string.IsNullOrWhiteSpace(title) ? "" : " · " + title);
        if (_examWindow is not null && _examText is not null)
        {
            _examText.Text = text;   // a new title while running
            return;
        }

        _examText = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x1A, 0x00)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 5, 16, 6),
        };
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xF2, 0xC2, 0x30)),
            CornerRadius = new CornerRadius(12),
            Child = _examText,
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = pill,
            Title = "PROCTOR exam",
        };
        w.SourceInitialized += (_, _) => MakeClickThrough(w);
        w.Loaded += (_, _) =>
        {
            // Bottom centre, above the taskbar (the remote-control banner uses the top).
            var area = SystemParameters.WorkArea;
            w.Left = area.Left + (area.Width - w.ActualWidth) / 2;
            w.Top = area.Bottom - w.ActualHeight - 6;
        };
        _examWindow = w;
        w.Show();
        _logger.LogInformation("Exam mode started: {Title}", title ?? "(no title)");
    });
}
