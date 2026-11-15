using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace ClassInsight.Agent.Services;

/// <summary>
/// Everything the student sees from ClassInsight (P5), on one WPF UI thread in the student's session:
///
///  * <b>Messages</b> from the teacher (FR-CTRL-03) — a centred, topmost card with an OK button.
///  * <b>Lock screen</b> (FR-CTRL-01) — a full-screen "eyes on your teacher" cover on every display,
///    kept on top, with keyboard input swallowed by a low-level hook while locked. Fail-safes:
///    it releases on Unlock, when the agent loses the server, and after
///    <see cref="Shared.Contracts.ClassroomLimits.MaxLockDuration"/>; Ctrl+Alt+Del always works.
///  * <b>Notices</b> — a short-lived pill ("youtube.com is blocked by your teacher", restart countdown…).
///  * <b>Raise hand</b> — a tray icon the student double-clicks (or right-clicks) to ask for help.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ClassroomUi : IDisposable
{
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Dispatcher? _dispatcher;

    private readonly List<Window> _lockWindows = new();
    private DispatcherTimer? _lockKeeper;
    private DateTime _lockExpires;
    private IntPtr _keyboardHook;
    private LowLevelKeyboardProc? _hookProc;   // kept alive while hooked

    private Window? _message;
    private Window? _notice;
    private DispatcherTimer? _noticeTimer;

    private Forms.NotifyIcon? _tray;
    private bool _handRaised;

    /// <summary>Raised (on the UI thread) when the student raises or lowers their hand from the tray.</summary>
    public event Action<bool>? HandToggled;

    public ClassroomUi(ILogger logger)
    {
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClassInsight student UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsLocked { get; private set; }

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            CreateTray();
            _ready.Set();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Student UI unavailable (no interactive desktop?).");
            _dispatcher = null;
            _ready.Set();
        }
    }

    private void Post(Action action)
    {
        try { _dispatcher?.BeginInvoke(action); }
        catch (Exception ex) { _logger.LogDebug(ex, "UI post failed."); }
    }

    // ---- messages ----------------------------------------------------------

    public void ShowMessage(string text) => Post(() =>
    {
        _message?.Close();
        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        body.Children.Add(new TextBlock
        {
            Text = "Message from your teacher",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0x94, 0xF6)),
            Margin = new Thickness(0, 0, 0, 10),
        });
        body.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 18,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        });
        var ok = new Button
        {
            Content = "OK",
            Padding = new Thickness(26, 6, 26, 6),
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
        };
        body.Children.Add(ok);

        var window = Card(body);
        ok.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_message, window)) _message = null; };
        _message = window;
        window.Show();
        window.Activate();
    });

    // ---- lock screen -------------------------------------------------------

    public void Lock(string? caption) => Post(() =>
    {
        _lockExpires = DateTime.UtcNow + Shared.Contracts.ClassroomLimits.MaxLockDuration;
        if (IsLocked)
        {
            foreach (var w in _lockWindows) SetCaption(w, caption);
            return;
        }

        IsLocked = true;
        foreach (var display in DisplayEnumerator.GetDisplays())
        {
            var w = BuildLockWindow(display, caption);
            _lockWindows.Add(w);
            w.Show();
        }
        _lockWindows.FirstOrDefault()?.Activate();
        InstallKeyboardHook();

        _lockKeeper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockKeeper.Tick += (_, _) =>
        {
            if (DateTime.UtcNow > _lockExpires)
            {
                _logger.LogWarning("Lock expired after {Hours} h; releasing.", Shared.Contracts.ClassroomLimits.MaxLockDuration.TotalHours);
                UnlockNow();
                return;
            }
            // Stay above everything, including other topmost windows (task manager, start menu…).
            foreach (var w in _lockWindows) { w.Topmost = false; w.Topmost = true; }
        };
        _lockKeeper.Start();
        _logger.LogInformation("Screen locked by the teacher.");
    });

    public void Unlock() => Post(UnlockNow);

    private void UnlockNow()
    {
        if (!IsLocked) return;
        IsLocked = false;
        _lockKeeper?.Stop();
        _lockKeeper = null;
        RemoveKeyboardHook();
        foreach (var w in _lockWindows) { w.Tag = "closing"; w.Close(); }
        _lockWindows.Clear();
        _logger.LogInformation("Screen unlocked.");
    }

    private Window BuildLockWindow(CaptureDisplay display, string? caption)
    {
        var title = new TextBlock
        {
            Text = "🔒  Eyes on your teacher",
            FontSize = 40,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var sub = new TextBlock
        {
            Name = "Caption",
            Text = caption ?? "Your screen has been locked by your teacher.",
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCC, 0xD1)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 900,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(sub);

        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x12, 0x1E)),
            Content = stack,
            Title = "ClassInsight",
            Cursor = Cursors.None,
        };
        // Size it to the display in physical pixels once the HWND exists (per-monitor DPI aware).
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, display.Left, display.Top, display.Width, display.Height, SWP_SHOWWINDOW);
        };
        w.Closing += (_, e) => { if (w.Tag as string != "closing") e.Cancel = true; };   // Alt+F4 does nothing
        return w;
    }

    private static void SetCaption(Window w, string? caption)
    {
        if (w.Content is StackPanel s && s.Children.Count > 1 && s.Children[1] is TextBlock t)
            t.Text = caption ?? "Your screen has been locked by your teacher.";
    }

    // Low-level keyboard hook: while locked, every key is swallowed (Ctrl+Alt+Del is handled by
    // Windows itself and can never be blocked — the student can always reach the secure screen).
    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _hookProc = (code, wParam, lParam) => code >= 0 && IsLocked ? (IntPtr)1 : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        using var module = Process.GetCurrentProcess().MainModule;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(module?.ModuleName), 0);
        if (_keyboardHook == IntPtr.Zero)
            _logger.LogWarning("Could not install the keyboard lock ({Error}); the lock screen still covers the display.", Marshal.GetLastWin32Error());
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _hookProc = null;
    }

    // ---- notices -----------------------------------------------------------

    /// <summary>A short-lived pill at the top of the primary display.</summary>
    public void ShowNotice(string text, TimeSpan? duration = null) => Post(() =>
    {
        _notice?.Close();
        _noticeTimer?.Stop();
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x2A, 0x22, 0x3D)),
            CornerRadius = new CornerRadius(14),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(18, 8, 18, 9),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640,
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
        };
        w.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            w.Left = area.Left + (area.Width - w.ActualWidth) / 2;
            w.Top = area.Top + 56;
        };
        _notice = w;
        w.Show();
        _noticeTimer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer?.Stop(); w.Close(); };
        _noticeTimer.Start();
    });

    // ---- raise hand (tray) -------------------------------------------------

    private void CreateTray()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            var toggle = new Forms.ToolStripMenuItem("✋ Raise hand (ask for help)");
            toggle.Click += (_, _) => ToggleHand();
            menu.Items.Add(toggle);
            menu.Opening += (_, _) => toggle.Text = _handRaised ? "Lower my hand" : "✋ Raise hand (ask for help)";

            _tray = new Forms.NotifyIcon
            {
                Icon = BuildTrayIcon(),
                Text = "ClassInsight — double-click to ask your teacher for help",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => ToggleHand();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No tray icon (no shell in this session).");
        }
    }

    private void ToggleHand()
    {
        _handRaised = !_handRaised;
        HandToggled?.Invoke(_handRaised);
        _tray?.ShowBalloonTip(3000, "ClassInsight",
            _handRaised ? "Your teacher has been told you need help." : "You lowered your hand.",
            Forms.ToolTipIcon.Info);
    }

    /// <summary>The teacher acknowledged the request (or the connection reset).</summary>
    public void ResetHand(bool showNotice) => Post(() =>
    {
        if (!_handRaised) return;
        _handRaised = false;
        if (showNotice) ShowNotice("✋  Your teacher saw your raised hand.");
    });

    private static System.Drawing.Icon BuildTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x7E, 0x57, 0xC2));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new System.Drawing.Font("Segoe UI", 15, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var size = g.MeasureString("C", font);
            g.DrawString("C", font, System.Drawing.Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    // ---- helpers ------------------------------------------------------------

    private static Window Card(UIElement content)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1A, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7E, 0x57, 0xC2)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(14),
                Child = content,
                MinWidth = 380,
            },
            Title = "ClassInsight",
        };
        return w;
    }

    public void Dispose()
    {
        if (_dispatcher is not null)
        {
            try
            {
                _dispatcher.Invoke(() =>
                {
                    UnlockNow();
                    if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
                });
                _dispatcher.InvokeShutdown();
            }
            catch { }
        }
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    // ---- interop -----------------------------------------------------------

    private const int WH_KEYBOARD_LL = 13;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
