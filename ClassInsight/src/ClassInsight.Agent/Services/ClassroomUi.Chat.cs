using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClassInsight.Agent.Services;

/// <summary>Student side of teacher ↔ student chat: a small window, opened by the teacher's message or from the tray.</summary>
internal sealed partial class ClassroomUi
{
    private Window? _chatWindow;
    private StackPanel? _chatLines;
    private ScrollViewer? _chatScroll;
    private TextBox? _chatInput;

    /// <summary>Raised (UI thread) when the student sends a chat line.</summary>
    public event Action<string>? ChatSent;

    /// <summary>A line from the teacher (or the student's own line echoed back after an error).</summary>
    public void ShowChat(Shared.Contracts.ChatMessage message) => Post(() =>
    {
        EnsureChatWindow();
        AddChatLine(message.FromTeacher ? "Teacher" : "You", message.Text, message.FromTeacher);
        if (!_chatWindow!.IsVisible)
        {
            _chatWindow.Show();      // ShowActivated=false: never steals the student's typing
            PlaceBottomRight(_chatWindow);
        }
        _chatWindow.Topmost = true;
    });

    /// <summary>The connection reset: show a line so the student knows their message may not arrive.</summary>
    public void ChatDisconnected() => Post(() =>
    {
        if (_chatWindow is { IsVisible: true }) AddChatLine("ClassInsight", "Lost the connection to the teacher.", true, system: true);
    });

    private void OpenChatFromTray() => Post(() =>
    {
        EnsureChatWindow();
        _chatWindow!.Show();
        PlaceBottomRight(_chatWindow);
        _chatWindow.Activate();
        _chatInput?.Focus();
    });

    private void EnsureChatWindow()
    {
        if (_chatWindow is not null) return;

        _chatLines = new StackPanel { Margin = new Thickness(10) };
        _chatScroll = new ScrollViewer { Content = _chatLines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 260 };
        _chatInput = new TextBox
        {
            MaxLength = Shared.Contracts.ClassroomLimits.MaxTextLength,
            FontSize = 14,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _chatInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SendChatLine(); e.Handled = true; } };
        var send = new Button { Content = "Send", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(6, 0, 0, 0) };
        send.Click += (_, _) => SendChatLine();

        var input = new DockPanel { Margin = new Thickness(10, 0, 10, 10) };
        DockPanel.SetDock(send, Dock.Right);
        input.Children.Add(send);
        input.Children.Add(_chatInput);

        var root = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1A, 0x26)) };
        DockPanel.SetDock(input, Dock.Bottom);
        root.Children.Add(input);
        root.Children.Add(_chatScroll);

        _chatWindow = new Window
        {
            Title = "Chat with your teacher",
            Content = root,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = true,
        };
        // Closing just hides it, so the conversation is still there next time.
        _chatWindow.Closing += (_, e) => { if (_chatWindow?.Tag as string != "dispose") { e.Cancel = true; _chatWindow!.Hide(); } };
    }

    private void SendChatLine()
    {
        var text = Shared.Contracts.ClassroomLimits.CleanText(_chatInput?.Text);
        if (text is null) return;
        _chatInput!.Text = "";
        AddChatLine("You", text, fromTeacher: false);
        ChatSent?.Invoke(text);
    }

    /// <summary>Tell the student a line couldn't be delivered.</summary>
    public void ChatFailed(string reason) => Post(() => AddChatLine("ClassInsight", "Not sent: " + reason, true, system: true));

    private void AddChatLine(string who, string text, bool fromTeacher, bool system = false)
    {
        if (_chatLines is null) return;
        var bubble = new Border
        {
            Background = new SolidColorBrush(system ? Color.FromRgb(0x3A, 0x2A, 0x2A)
                : fromTeacher ? Color.FromRgb(0x2E, 0x2A, 0x45) : Color.FromRgb(0x5B, 0x2C, 0x9E)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 7),
            Margin = new Thickness(fromTeacher ? 0 : 40, 3, fromTeacher ? 40 : 0, 3),
            HorizontalAlignment = fromTeacher ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{who} · {DateTime.Now:HH:mm}", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xA8, 0xC8)) },
                    new TextBlock { Text = text, FontSize = 14, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        _chatLines.Children.Add(bubble);
        while (_chatLines.Children.Count > Shared.Contracts.ChatLimits.MaxHistory) _chatLines.Children.RemoveAt(0);
        _chatScroll?.ScrollToEnd();
    }

    private static void PlaceBottomRight(Window w)
    {
        w.UpdateLayout();
        var area = SystemParameters.WorkArea;
        w.Left = area.Right - w.ActualWidth - 16;
        w.Top = area.Bottom - w.ActualHeight - 16;
    }
}
