using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent.Services;

/// <summary>
/// "Freeze keyboard and mouse": the student's own keyboard and mouse stop working but the screen
/// stays visible (watching a demo or the teacher's shared screen). A small bar says why. Input the
/// teacher sends through remote control still works (it is marked as injected), and Ctrl+Alt+Del
/// always reaches Windows. Released by Unlock, a lost connection, the teacher leaving, or after
/// <see cref="Shared.Contracts.ClassroomLimits.MaxLockDuration"/> at the latest.
/// </summary>
internal sealed partial class ClassroomUi
{
    private IntPtr _freezeKeyboardHook, _freezeMouseHook;
    private LowLevelKeyboardProc? _freezeKeyboardProc;   // kept alive while hooked
    private LowLevelKeyboardProc? _freezeMouseProc;
    private Window? _freezeWindow;
    private DispatcherTimer? _freezeKeeper;
    private DateTime _freezeExpires;

    public bool IsFrozen { get; private set; }

    public void Freeze(string? caption) => Post(() =>
    {
        _freezeExpires = DateTime.UtcNow + Shared.Contracts.ClassroomLimits.MaxLockDuration;
        var text = "⏸  " + (caption ?? "Your teacher has paused your keyboard and mouse. Watch the screen.");
        if (IsFrozen)
        {
            if (_freezeWindow?.Content is Border { Child: TextBlock t }) t.Text = text;
            return;
        }

        IsFrozen = true;
        InstallFreezeHooks();
        ShowFreezeBar(text);
        _freezeKeeper = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _freezeKeeper.Tick += (_, _) =>
        {
            if (DateTime.UtcNow > _freezeExpires)
            {
                _logger.LogWarning("Keyboard/mouse freeze expired; releasing.");
                UnfreezeNow();
                return;
            }
            if (_freezeWindow is { } w) { w.Topmost = false; w.Topmost = true; }
        };
        _freezeKeeper.Start();
        _logger.LogInformation("Keyboard and mouse frozen by the teacher.");
    });

    public void Unfreeze() => Post(UnfreezeNow);

    private void UnfreezeNow()
    {
        if (!IsFrozen) return;
        IsFrozen = false;
        _freezeKeeper?.Stop();
        _freezeKeeper = null;
        RemoveFreezeHooks();
        _freezeWindow?.Close();
        _freezeWindow = null;
        _logger.LogInformation("Keyboard and mouse released.");
    }

    private void InstallFreezeHooks()
    {
        using var module = Process.GetCurrentProcess().MainModule;
        var handle = GetModuleHandle(module?.ModuleName);
        if (_freezeKeyboardHook == IntPtr.Zero)
        {
            // KBDLLHOOKSTRUCT.flags (offset 8): LLKHF_INJECTED = 0x10 — the teacher's remote control.
            _freezeKeyboardProc = (code, wParam, lParam) =>
                code >= 0 && IsFrozen && (Marshal.ReadInt32(lParam, 8) & 0x10) == 0
                    ? (IntPtr)1
                    : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            _freezeKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _freezeKeyboardProc, handle, 0);
        }
        if (_freezeMouseHook == IntPtr.Zero)
        {
            // MSLLHOOKSTRUCT.flags (offset 12): LLMHF_INJECTED = 0x01.
            _freezeMouseProc = (code, wParam, lParam) =>
                code >= 0 && IsFrozen && (Marshal.ReadInt32(lParam, 12) & 0x01) == 0
                    ? (IntPtr)1
                    : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            _freezeMouseHook = SetWindowsHookEx(WH_MOUSE_LL, _freezeMouseProc, handle, 0);
        }
        if (_freezeKeyboardHook == IntPtr.Zero || _freezeMouseHook == IntPtr.Zero)
            _logger.LogWarning("Could not fully freeze the keyboard and mouse ({Error}).", Marshal.GetLastWin32Error());
    }

    private void RemoveFreezeHooks()
    {
        if (_freezeKeyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_freezeKeyboardHook);
        if (_freezeMouseHook != IntPtr.Zero) UnhookWindowsHookEx(_freezeMouseHook);
        _freezeKeyboardHook = _freezeMouseHook = IntPtr.Zero;
        _freezeKeyboardProc = _freezeMouseProc = null;
    }

    private void ShowFreezeBar(string text)
    {
        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x5B, 0x2C, 0x9E)),
            CornerRadius = new CornerRadius(12),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(18, 7, 18, 8),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 760,
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
            Title = "PROCTOR paused",
        };
        w.SourceInitialized += (_, _) => MakeClickThrough(w);
        w.Loaded += (_, _) =>
        {
            // Top centre, a little lower than the notices.
            var area = SystemParameters.WorkArea;
            w.Left = area.Left + (area.Width - w.ActualWidth) / 2;
            w.Top = area.Top + 56;
        };
        _freezeWindow = w;
        w.Show();
    }

    private const int WH_MOUSE_LL = 14;
}
