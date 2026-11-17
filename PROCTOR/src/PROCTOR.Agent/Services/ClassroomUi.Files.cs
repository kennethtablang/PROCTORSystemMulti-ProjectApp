using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PROCTOR.Agent.Services;

/// <summary>Student side of "collect work": a window listing the files in the student's Hand in folder.</summary>
internal sealed partial class ClassroomUi
{
    private Window? _handInWindow;
    private StackPanel? _handInList;
    private TextBlock? _handInStatus;
    private TextBlock? _handInPrompt;
    private bool _collecting;
    private readonly List<CheckBox> _handInChecks = new();

    /// <summary>Raised (UI thread): open the Hand in folder / re-read it / hand in these file names.</summary>
    public event Action? HandInFolderRequested;
    public event Action? HandInRefreshRequested;
    public event Action<IReadOnlyList<string>>? HandInSubmitted;

    /// <summary>The teacher started or stopped collecting work.</summary>
    public void ShowHandIn(Shared.Contracts.HandInInfo info) => Post(() =>
    {
        _collecting = info.Open;
        if (!info.Open)
        {
            if (_handInWindow is not null) { _handInWindow.Tag = "dispose"; _handInWindow.Close(); _handInWindow = null; }
            ShowNotice("📤  Your teacher has stopped collecting work.", TimeSpan.FromSeconds(4));
            return;
        }
        EnsureHandInWindow();
        _handInPrompt!.Text = string.IsNullOrEmpty(info.Prompt) ? "Your teacher is collecting work." : info.Prompt;
        _handInStatus!.Text = "";
        _handInWindow!.Show();
        _handInWindow.Activate();
        HandInRefreshRequested?.Invoke();
    });

    /// <summary>Connection lost: close the hand-in window quietly.</summary>
    public void ResetHandIn() => Post(() =>
    {
        _collecting = false;
        if (_handInWindow is not null) { _handInWindow.Tag = "dispose"; _handInWindow.Close(); _handInWindow = null; }
    });

    private void OpenHandInFromTray() => Post(() =>
    {
        if (!_collecting)
        {
            ShowNotice("📤  Your teacher isn't collecting work right now.", TimeSpan.FromSeconds(4));
            return;
        }
        EnsureHandInWindow();
        _handInWindow!.Show();
        _handInWindow.Activate();
        HandInRefreshRequested?.Invoke();
    });

    /// <summary>Show the files currently in the Hand in folder.</summary>
    public void SetHandInFiles(IReadOnlyList<(string Name, long Size)> files) => Post(() =>
    {
        if (_handInList is null) return;
        _handInList.Children.Clear();
        _handInChecks.Clear();
        if (files.Count == 0)
        {
            _handInList.Children.Add(new TextBlock
            {
                Text = "No files yet. Press “Open my Hand in folder”, put your work there, then press Refresh.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xA8, 0xC8)),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var (name, size) in files)
        {
            var check = new CheckBox
            {
                Content = $"{name}  ({Size(size)})",
                Tag = name,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 3, 0, 3),
                IsChecked = files.Count == 1,
            };
            _handInChecks.Add(check);
            _handInList.Children.Add(check);
        }
    });

    public void SetHandInStatus(string text) => Post(() => { if (_handInStatus is not null) _handInStatus.Text = text; });

    private void EnsureHandInWindow()
    {
        if (_handInWindow is not null) return;
        var white = Brushes.White;
        _handInPrompt = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = white, TextWrapping = TextWrapping.Wrap };
        var how = new TextBlock
        {
            Text = "Put your files in the “Hand in” folder in Documents, tick them, then press Hand in.",
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xA8, 0xC8)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
        };
        _handInList = new StackPanel();
        var scroll = new ScrollViewer { Content = _handInList, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _handInStatus = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA5, 0xD6, 0xA7)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        Button Make(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Make("Open my Hand in folder", () => HandInFolderRequested?.Invoke()));
        buttons.Children.Add(Make("Refresh", () => HandInRefreshRequested?.Invoke()));
        buttons.Children.Add(Make("Hand in", () =>
        {
            var chosen = _handInChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (chosen.Count == 0) { _handInStatus!.Text = "Tick the files you want to hand in."; return; }
            HandInSubmitted?.Invoke(chosen);
        }));

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(_handInPrompt);
        panel.Children.Add(how);
        panel.Children.Add(scroll);
        panel.Children.Add(buttons);
        panel.Children.Add(_handInStatus);

        _handInWindow = new Window
        {
            Title = "Hand in work",
            Content = panel,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1A, 0x26)),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        _handInWindow.Closing += (_, e) => { if (_handInWindow?.Tag as string != "dispose") { e.Cancel = true; _handInWindow!.Hide(); } };
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
