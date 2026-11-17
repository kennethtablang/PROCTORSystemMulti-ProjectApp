using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent.Services;

/// <summary>The always-visible "your teacher is controlling this computer" banner (FR-CTRL-05).</summary>
internal sealed partial class ClassroomUi
{
    private Window? _remoteWindow;

    public void ShowRemoteControl(bool active) => Post(() =>
    {
        if (!active)
        {
            _remoteWindow?.Close();
            _remoteWindow = null;
            return;
        }
        if (_remoteWindow is not null) return;
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xC6, 0x28, 0x28)),
            CornerRadius = new CornerRadius(14),
            Child = new TextBlock
            {
                Text = "\U0001F5B1  Your teacher is controlling this computer",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(18, 7, 18, 8),
            },
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
            Title = "PROCTOR remote control",
        };
        w.SourceInitialized += (_, _) => MakeClickThrough(w);
        w.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            w.Left = area.Left + (area.Width - w.ActualWidth) / 2;
            w.Top = area.Top + 8;
        };
        _remoteWindow = w;
        w.Show();
        _logger.LogInformation("A teacher is controlling this computer.");
    });
}
