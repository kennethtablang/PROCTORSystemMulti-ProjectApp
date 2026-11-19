using Android.Content;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Java.Lang;
using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;
using MauiPoint = Microsoft.Maui.Graphics.Point;
using MauiView = Microsoft.Maui.Controls.View;
using AView = Android.Views.View;

namespace PROCTORSystemMulti_ProjectApp.Droid;

/// <summary>
/// Remote control from an Android phone or tablet (FR-CTRL-05), sending the same mouse/keyboard
/// events as the Windows teacher app:
///  * Touch on the student's picture: tap = left click, hold = right click, drag = left-button
///    drag, two fingers = scroll wheel.
///  * A mouse (tablet with a mouse): moves, buttons and wheel as on a PC.
///  * The on-screen keyboard (<see cref="ShowKeyboard"/>) and hardware keyboards: keys go to the
///    student's PC as Windows virtual keys (US layout).
/// </summary>
public sealed class AndroidRemoteInputCapture : IRemoteInputCapture
{
    private const long HoldMs = 550;
    private const float DragSlopDp = 8, ScrollStepDp = 28;

    private KeySink? _sink;

    public bool IsSupported => true;
    public bool HasOnScreenKeyboard => true;

    public void ShowKeyboard()
    {
        if (_sink is not { } sink) return;
        sink.RequestFocus();
        var imm = (InputMethodManager?)sink.Context?.GetSystemService(Context.InputMethodService);
        imm?.ShowSoftInput(sink, ShowFlags.Forced);
    }

    public IDisposable Attach(MauiView surface, Func<MauiPoint, MauiPoint?> toFrame, Action<RemoteInputEvent> onEvent)
    {
        if (surface.Handler?.PlatformView is not AView view)
            throw new InvalidOperationException("The student's screen isn't showing yet.");
        if (Platform.CurrentActivity is not { } activity)
            throw new InvalidOperationException("The app isn't ready yet.");

        var density = view.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
        var handler = new Handler(Looper.MainLooper!);
        var last = new MauiPoint(0.5, 0.5);

        MauiPoint? Map(float px, float py) => toFrame(new MauiPoint(px / density, py / density));
        void Send(RemoteInputKind kind, MauiPoint at, RemoteMouseButton button = RemoteMouseButton.Left) =>
            onEvent(new RemoteInputEvent { Kind = kind, Button = button, X = at.X, Y = at.Y });

        // ---- touch / mouse on the picture ------------------------------------------------------
        var mode = Mode.None;
        MauiPoint startFrame = last;
        float startX = 0, startY = 0, scrollX = 0, scrollY = 0, accX = 0, accY = 0;
        var mouseButton = RemoteMouseButton.Left;

        void Hold()
        {
            if (mode != Mode.Pending) return;
            mode = Mode.Done;   // finger still down after a hold: right click, nothing more
            Send(RemoteInputKind.MouseDown, startFrame, RemoteMouseButton.Right);
            Send(RemoteInputKind.MouseUp, startFrame, RemoteMouseButton.Right);
            view.PerformHapticFeedback(FeedbackConstants.LongPress);
        }

        void OnTouch(object? s, AView.TouchEventArgs e)
        {
            if (e.Event is not { } ev) return;
            e.Handled = true;
            var isMouse = ev.GetToolType(0) == MotionEventToolType.Mouse;
            var action = ev.ActionMasked;

            if (isMouse)
            {
                var p = Map(ev.GetX(), ev.GetY());
                if (p is { } f) last = f;
                switch (action)
                {
                    case MotionEventActions.Down:
                        if (p is null) return;
                        mouseButton = (ev.ButtonState & MotionEventButtonState.Secondary) != 0 ? RemoteMouseButton.Right
                            : (ev.ButtonState & MotionEventButtonState.Tertiary) != 0 ? RemoteMouseButton.Middle
                            : RemoteMouseButton.Left;
                        Send(RemoteInputKind.MouseMove, last);
                        Send(RemoteInputKind.MouseDown, last, mouseButton);
                        break;
                    case MotionEventActions.Move:
                        if (p is not null) Send(RemoteInputKind.MouseMove, last);
                        break;
                    case MotionEventActions.Up:
                    case MotionEventActions.Cancel:
                        Send(RemoteInputKind.MouseUp, last, mouseButton);
                        break;
                }
                return;
            }

            switch (action)
            {
                case MotionEventActions.Down:
                {
                    if (Map(ev.GetX(), ev.GetY()) is not { } f) { mode = Mode.Done; return; }
                    startFrame = last = f;
                    startX = ev.GetX(); startY = ev.GetY();
                    mode = Mode.Pending;
                    Send(RemoteInputKind.MouseMove, f);
                    handler.PostDelayed(Hold, HoldMs);
                    break;
                }
                case MotionEventActions.PointerDown:
                    handler.RemoveCallbacksAndMessages(null);
                    if (mode == Mode.Dragging) Send(RemoteInputKind.MouseUp, last);
                    mode = Mode.Scrolling;
                    (scrollX, scrollY) = Average(ev);
                    accX = accY = 0;
                    break;
                case MotionEventActions.Move:
                    if (mode == Mode.Scrolling)
                    {
                        var (ax, ay) = Average(ev);
                        accX += (ax - scrollX) / density; accY += (ay - scrollY) / density;
                        scrollX = ax; scrollY = ay;
                        // Finger up = content moves up = wheel "down" (negative), as on a touch screen.
                        while (System.Math.Abs(accY) >= ScrollStepDp)
                        {
                            var sign = accY > 0 ? 1 : -1;
                            onEvent(new RemoteInputEvent { Kind = RemoteInputKind.Wheel, Delta = sign * 120, X = last.X, Y = last.Y });
                            accY -= sign * ScrollStepDp;
                        }
                        while (System.Math.Abs(accX) >= ScrollStepDp)
                        {
                            var sign = accX > 0 ? 1 : -1;
                            onEvent(new RemoteInputEvent { Kind = RemoteInputKind.Wheel, Delta = -sign * 120, Horizontal = true, X = last.X, Y = last.Y });
                            accX -= sign * ScrollStepDp;
                        }
                        break;
                    }
                    if (mode == Mode.Pending)
                    {
                        var moved = System.Math.Max(System.Math.Abs(ev.GetX() - startX), System.Math.Abs(ev.GetY() - startY)) / density;
                        if (moved < DragSlopDp) break;
                        handler.RemoveCallbacksAndMessages(null);
                        mode = Mode.Dragging;
                        Send(RemoteInputKind.MouseDown, startFrame);
                    }
                    if (mode == Mode.Dragging && Map(ev.GetX(), ev.GetY()) is { } m)
                    {
                        last = m;
                        Send(RemoteInputKind.MouseMove, m);
                    }
                    break;
                case MotionEventActions.Up:
                    handler.RemoveCallbacksAndMessages(null);
                    if (mode == Mode.Pending)
                    {
                        Send(RemoteInputKind.MouseDown, startFrame);
                        Send(RemoteInputKind.MouseUp, startFrame);
                    }
                    else if (mode == Mode.Dragging)
                    {
                        Send(RemoteInputKind.MouseUp, Map(ev.GetX(), ev.GetY()) ?? last);
                    }
                    mode = Mode.None;
                    break;
                case MotionEventActions.Cancel:
                    handler.RemoveCallbacksAndMessages(null);
                    if (mode == Mode.Dragging) Send(RemoteInputKind.MouseUp, last);
                    mode = Mode.None;
                    break;
            }
        }

        // Mouse hover and wheel arrive as "generic motion".
        void OnGenericMotion(object? s, AView.GenericMotionEventArgs e)
        {
            if (e.Event is not { } ev) return;
            if (ev.ActionMasked == MotionEventActions.HoverMove && Map(ev.GetX(), ev.GetY()) is { } f)
            {
                last = f;
                Send(RemoteInputKind.MouseMove, f);
                e.Handled = true;
            }
            else if (ev.ActionMasked == MotionEventActions.Scroll)
            {
                var v = ev.GetAxisValue(Axis.Vscroll);
                var h = ev.GetAxisValue(Axis.Hscroll);
                if (v != 0) onEvent(new RemoteInputEvent { Kind = RemoteInputKind.Wheel, Delta = (int)System.Math.Clamp(v * 120, -1200, 1200), X = last.X, Y = last.Y });
                if (h != 0) onEvent(new RemoteInputEvent { Kind = RemoteInputKind.Wheel, Delta = (int)System.Math.Clamp(h * 120, -1200, 1200), Horizontal = true, X = last.X, Y = last.Y });
                e.Handled = true;
            }
        }

        // ---- keyboards ---------------------------------------------------------------------------
        void Key(int vk, bool down) =>
            onEvent(new RemoteInputEvent { Kind = down ? RemoteInputKind.KeyDown : RemoteInputKind.KeyUp, VirtualKey = vk, X = last.X, Y = last.Y });

        bool OnKey(KeyEvent e)
        {
            if (KeyMap.FromKeycode((int)e.KeyCode) is not { } vk) return false;   // Back, volume… stay with Android
            if (e.Action == KeyEventActions.Down) Key(vk, true);
            else if (e.Action == KeyEventActions.Up) Key(vk, false);
            return true;
        }

        void TypeChar(char c)
        {
            if (KeyMap.FromChar(c) is not { } k) return;   // characters with no US-keyboard key (é, emoji)
            if (k.Shift) Key(KeyMap.Shift, true);
            Key(k.Vk, true);
            Key(k.Vk, false);
            if (k.Shift) Key(KeyMap.Shift, false);
        }

        // An invisible view that owns the on-screen keyboard while in control.
        var content = activity.FindViewById<ViewGroup>(Android.Resource.Id.Content);
        var sink = new KeySink(activity, TypeChar, OnKey) { Alpha = 0f };
        content?.AddView(sink, new ViewGroup.LayoutParams(1, 1));
        _sink = sink;

        view.Touch += OnTouch;
        view.GenericMotion += OnGenericMotion;
        var previousInterceptor = MainActivity.KeyInterceptor;
        MainActivity.KeyInterceptor = OnKey;

        return new Detach(() =>
        {
            handler.RemoveCallbacksAndMessages(null);
            view.Touch -= OnTouch;
            view.GenericMotion -= OnGenericMotion;
            MainActivity.KeyInterceptor = previousInterceptor;
            try
            {
                var imm = (InputMethodManager?)sink.Context?.GetSystemService(Context.InputMethodService);
                imm?.HideSoftInputFromWindow(sink.WindowToken, HideSoftInputFlags.None);
            }
            catch { }
            content?.RemoveView(sink);
            if (ReferenceEquals(_sink, sink)) _sink = null;
        });
    }

    private static (float X, float Y) Average(MotionEvent ev)
    {
        float x = 0, y = 0;
        var n = System.Math.Max(1, ev.PointerCount);
        for (var i = 0; i < ev.PointerCount; i++) { x += ev.GetX(i); y += ev.GetY(i); }
        return (x / n, y / n);
    }

    private enum Mode { None, Pending, Dragging, Scrolling, Done }

    private sealed class Detach(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }

    /// <summary>
    /// Receives the on-screen keyboard's input. Keyboards that compose words (suggestions) send the
    /// whole word again on every letter, so each update is compared with the previous one and only
    /// the difference is typed (backspaces + new characters).
    /// </summary>
    private sealed class KeySink : AView
    {
        private readonly Action<char> _type;
        private readonly Func<KeyEvent, bool> _key;

        public KeySink(Context context, Action<char> type, Func<KeyEvent, bool> key) : base(context)
        {
            _type = type;
            _key = key;
            Focusable = true;
            FocusableInTouchMode = true;
        }

        public override bool OnCheckIsTextEditor() => true;

        public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
        {
            if (outAttrs is not null)
            {
                // No suggestions/auto-correct: keys should reach the PC as typed.
                outAttrs.InputType = InputTypes.ClassText | InputTypes.TextVariationVisiblePassword | InputTypes.TextFlagNoSuggestions;
                outAttrs.ImeOptions = ImeFlags.NoExtractUi | ImeFlags.NoFullscreen | (ImeFlags)ImeAction.None;
            }
            return new Connection(this);
        }

        private sealed class Connection(KeySink owner) : BaseInputConnection(owner, false)
        {
            private string _composing = "";

            public override bool CommitText(ICharSequence? text, int newCursorPosition)
            {
                Replace(text?.ToString() ?? "");
                _composing = "";
                return true;
            }

            public override bool SetComposingText(ICharSequence? text, int newCursorPosition)
            {
                Replace(text?.ToString() ?? "");
                _composing = text?.ToString() ?? "";
                return true;
            }

            public override bool FinishComposingText()
            {
                _composing = "";
                return true;
            }

            public override bool DeleteSurroundingText(int beforeLength, int afterLength)
            {
                for (var i = 0; i < System.Math.Max(1, beforeLength); i++) BackSpace();
                return true;
            }

            public override bool SendKeyEvent(KeyEvent? e)
            {
                if (e is not null) owner._key(e);   // Enter, Delete… from the on-screen keyboard
                return true;
            }

            private void Replace(string text)
            {
                var common = 0;
                while (common < _composing.Length && common < text.Length && _composing[common] == text[common]) common++;
                for (var i = common; i < _composing.Length; i++) BackSpace();
                foreach (var c in text.AsSpan(common)) owner._type(c);
            }

            private void BackSpace() => owner._type('\b');
        }
    }
}

/// <summary>Android keys and typed characters → Windows virtual-key codes (US layout).</summary>
internal static class KeyMap
{
    public const int Shift = 0x10;

    public readonly record struct Stroke(int Vk, bool Shift);

    public static int? FromKeycode(int code) => code switch
    {
        >= 29 and <= 54 => 0x41 + (code - 29),     // A–Z
        >= 7 and <= 16 => 0x30 + (code - 7),       // 0–9
        >= 144 and <= 153 => 0x60 + (code - 144),  // numpad 0–9
        >= 131 and <= 142 => 0x70 + (code - 131),  // F1–F12
        66 or 160 => 0x0D,     // Enter, numpad Enter
        67 => 0x08,            // Delete (backspace)
        112 => 0x2E,           // Forward delete
        61 => 0x09,            // Tab
        62 => 0x20,            // Space
        111 => 0x1B,           // Escape
        19 => 0x26, 20 => 0x28, 21 => 0x25, 22 => 0x27,   // arrows up/down/left/right
        59 or 60 => 0x10,      // Shift
        113 or 114 => 0x11,    // Ctrl
        57 or 58 => 0x12,      // Alt
        117 or 118 => 0x5B,    // Windows / Meta
        115 => 0x14,           // Caps Lock
        122 => 0x24, 123 => 0x23, 92 => 0x21, 93 => 0x22, 124 => 0x2D,   // Home End PgUp PgDn Insert
        74 => 0xBA, 70 => 0xBB, 55 => 0xBC, 69 => 0xBD, 56 => 0xBE, 76 => 0xBF,   // ; = , - . /
        68 => 0xC0, 71 => 0xDB, 73 => 0xDC, 72 => 0xDD, 75 => 0xDE,              // ` [ \ ] '
        154 => 0x6F, 155 => 0x6A, 156 => 0x6D, 157 => 0x6B, 158 => 0x6E,        // numpad / * - + .
        _ => null,
    };

    public static Stroke? FromChar(char c) => c switch
    {
        >= 'a' and <= 'z' => new Stroke(0x41 + (c - 'a'), false),
        >= 'A' and <= 'Z' => new Stroke(0x41 + (c - 'A'), true),
        >= '0' and <= '9' => new Stroke(0x30 + (c - '0'), false),
        ' ' => new Stroke(0x20, false),
        '\n' => new Stroke(0x0D, false),
        '\t' => new Stroke(0x09, false),
        '\b' => new Stroke(0x08, false),
        '!' => new Stroke(0x31, true), '@' => new Stroke(0x32, true), '#' => new Stroke(0x33, true),
        '$' => new Stroke(0x34, true), '%' => new Stroke(0x35, true), '^' => new Stroke(0x36, true),
        '&' => new Stroke(0x37, true), '*' => new Stroke(0x38, true), '(' => new Stroke(0x39, true),
        ')' => new Stroke(0x30, true),
        ';' => new Stroke(0xBA, false), ':' => new Stroke(0xBA, true),
        '=' => new Stroke(0xBB, false), '+' => new Stroke(0xBB, true),
        ',' => new Stroke(0xBC, false), '<' => new Stroke(0xBC, true),
        '-' => new Stroke(0xBD, false), '_' => new Stroke(0xBD, true),
        '.' => new Stroke(0xBE, false), '>' => new Stroke(0xBE, true),
        '/' => new Stroke(0xBF, false), '?' => new Stroke(0xBF, true),
        '`' => new Stroke(0xC0, false), '~' => new Stroke(0xC0, true),
        '[' => new Stroke(0xDB, false), '{' => new Stroke(0xDB, true),
        '\\' => new Stroke(0xDC, false), '|' => new Stroke(0xDC, true),
        ']' => new Stroke(0xDD, false), '}' => new Stroke(0xDD, true),
        '\'' => new Stroke(0xDE, false), '"' => new Stroke(0xDE, true),
        _ => null,
    };
}
