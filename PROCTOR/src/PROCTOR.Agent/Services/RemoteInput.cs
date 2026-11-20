using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PROCTOR.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Plays a controlling teacher's mouse and keyboard into the student's desktop with SendInput
/// (FR-CTRL-05). Positions arrive normalised to one display and are mapped onto the virtual desktop
/// in physical pixels (the agent is per-monitor DPI aware). Every key and button still held is
/// released when control ends, so nothing is left "stuck down".
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RemoteInput
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<ushort> _keysDown = new();
    private readonly HashSet<RemoteMouseButton> _buttonsDown = new();

    public RemoteInput(ILogger logger) => _logger = logger;

    public void Apply(RemoteInputBatch batch)
    {
        var displays = DisplayEnumerator.GetDisplays();
        var d = displays[Math.Clamp(batch.DisplayIndex, 0, displays.Count - 1)];
        var inputs = new List<INPUT>(batch.Events.Count);
        lock (_gate)
        {
            foreach (var e in batch.Events.Take(RemoteControlLimits.MaxEventsPerBatch))
            {
                if (!RemoteControlLimits.IsValid(e)) continue;
                switch (e.Kind)
                {
                    case RemoteInputKind.MouseMove:
                        inputs.Add(Mouse(d, e, MOUSEEVENTF_MOVE));
                        break;
                    case RemoteInputKind.MouseDown:
                        _buttonsDown.Add(e.Button);
                        inputs.Add(Mouse(d, e, MOUSEEVENTF_MOVE | ButtonFlag(e.Button, down: true)));
                        break;
                    case RemoteInputKind.MouseUp:
                        _buttonsDown.Remove(e.Button);
                        inputs.Add(Mouse(d, e, MOUSEEVENTF_MOVE | ButtonFlag(e.Button, down: false)));
                        break;
                    case RemoteInputKind.Wheel:
                        inputs.Add(new INPUT
                        {
                            type = INPUT_MOUSE,
                            u = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)e.Delta), dwFlags = e.Horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL } },
                        });
                        break;
                    case RemoteInputKind.KeyDown:
                        _keysDown.Add((ushort)e.VirtualKey);
                        inputs.Add(Key((ushort)e.VirtualKey, up: false));
                        break;
                    case RemoteInputKind.KeyUp:
                        _keysDown.Remove((ushort)e.VirtualKey);
                        inputs.Add(Key((ushort)e.VirtualKey, up: true));
                        break;
                }
            }
        }
        Send(inputs);
    }

    /// <summary>Control ended: let go of every key and mouse button the teacher was holding.</summary>
    public void ReleaseAll()
    {
        var inputs = new List<INPUT>();
        lock (_gate)
        {
            foreach (var vk in _keysDown) inputs.Add(Key(vk, up: true));
            foreach (var b in _buttonsDown)
                inputs.Add(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = ButtonFlag(b, down: false) } } });
            _keysDown.Clear();
            _buttonsDown.Clear();
        }
        Send(inputs);
    }

    // One-time tags on the events the teacher's remote control plays in (hooks see them as
    // dwExtraInfo). "Freeze keyboard and mouse" lets an injected event through only when its tag is
    // one we just issued, and uses it up: a program that watches the tags can't replay them, and
    // input faked by other programs (no valid tag) stays blocked.
    // Each tag is bound to the event it was issued for (a key's virtual-key code, or "mouse"), so a
    // stolen tag can only ever deliver that same event.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, (long Expires, int Event)> Pending = new();
    private static readonly long TagLifetime = TimeSpan.FromMilliseconds(500).Ticks;
    private const int MouseEvent = -1;

    private static IntPtr IssueTag(int forEvent)
    {
        var now = DateTime.UtcNow.Ticks;
        if (Pending.Count > 2000)
            foreach (var (tag, v) in Pending)
                if (v.Expires < now) Pending.TryRemove(tag, out _);
        IntPtr t;
        do t = (IntPtr)Random.Shared.NextInt64(1, long.MaxValue);
        while (!Pending.TryAdd(t, (now + TagLifetime, forEvent)));
        return t;
    }

    /// <summary>True (once) when <paramref name="tag"/> was just issued for this key (<paramref name="vk"/>) or, with null, a mouse event.</summary>
    public static bool ConsumeTag(IntPtr tag, uint? vk) =>
        tag != IntPtr.Zero && Pending.TryRemove(tag, out var v) && v.Expires >= DateTime.UtcNow.Ticks
        && v.Event == (vk is { } k ? (int)k : MouseEvent);

    private void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        for (var i = 0; i < arr.Length; i++)
        {
            if (arr[i].type == INPUT_MOUSE) arr[i].u.mi.dwExtraInfo = IssueTag(MouseEvent);
            else if (arr[i].type == INPUT_KEYBOARD) arr[i].u.ki.dwExtraInfo = IssueTag(arr[i].u.ki.wVk);
        }
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length)
            _logger.LogDebug("SendInput delivered {Sent} of {Count} events (error {Error}; secure desktop or UAC prompt?).",
                sent, arr.Length, Marshal.GetLastWin32Error());
    }

    private static INPUT Mouse(CaptureDisplay d, RemoteInputEvent e, uint flags)
    {
        var px = d.Left + e.X * (d.Width - 1);
        var py = d.Top + e.Y * (d.Height - 1);
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        return new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((px - vx) * 65535.0 / (vw - 1)),
                    dy = (int)Math.Round((py - vy) * 65535.0 / (vh - 1)),
                    dwFlags = flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
    }

    private static uint ButtonFlag(RemoteMouseButton b, bool down) => b switch
    {
        RemoteMouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        RemoteMouseButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
    };

    private static INPUT Key(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0u;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC), dwFlags = flags } },
        };
    }

    private static bool IsExtended(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PgUp PgDn End Home arrows
        or 0x2C or 0x2D or 0x2E                                      // PrintScreen Insert Delete
        or 0x5B or 0x5C or 0x5D                                      // Win keys, Apps
        or 0x6F or 0x90 or 0xA3 or 0xA5;                             // Num /, NumLock, RCtrl, RAlt

    // ---- Win32 -------------------------------------------------------------

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
        MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
