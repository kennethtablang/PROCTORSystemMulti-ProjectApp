using PROCTOR.Shared.Contracts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PROCTORSystemMulti_ProjectApp.PROCTOR;
using MauiPoint = Microsoft.Maui.Graphics.Point;
using MauiView = Microsoft.Maui.Controls.View;

namespace PROCTORSystemMulti_ProjectApp.WinUI;

/// <summary>
/// Windows teacher app: captures mouse over the student's picture and every key pressed while
/// remote control is on (keys are swallowed so they don't also act on the teacher app).
/// </summary>
public sealed class WindowsRemoteInputCapture : IRemoteInputCapture
{
    public bool IsSupported => true;

    public IDisposable Attach(MauiView surface, Func<MauiPoint, MauiPoint?> toFrame, Action<RemoteInputEvent> onEvent)
    {
        if (surface.Handler?.PlatformView is not UIElement element)
            throw new InvalidOperationException("The student's screen isn't showing yet.");
        var root = (surface.Window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window)?.Content
            ?? throw new InvalidOperationException("The app window isn't ready.");

        // A panel with no background isn't hit-testable; give it a transparent one.
        if (element is Microsoft.UI.Xaml.Controls.Panel { Background: null } panel)
            panel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var last = new MauiPoint(0.5, 0.5);

        MauiPoint? Map(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(element).Position;
            return toFrame(new MauiPoint(p.X, p.Y));
        }

        void Moved(object s, PointerRoutedEventArgs e)
        {
            if (Map(e) is not { } f) return;
            last = f;
            onEvent(new RemoteInputEvent { Kind = RemoteInputKind.MouseMove, X = f.X, Y = f.Y });
            e.Handled = true;
        }

        void Pressed(object s, PointerRoutedEventArgs e)
        {
            if (Map(e) is not { } f) return;
            last = f;
            if (Button(e.GetCurrentPoint(element).Properties.PointerUpdateKind) is not { } b) return;
            element.CapturePointer(e.Pointer);
            onEvent(new RemoteInputEvent { Kind = RemoteInputKind.MouseDown, Button = b, X = f.X, Y = f.Y });
            e.Handled = true;
        }

        void Released(object s, PointerRoutedEventArgs e)
        {
            var f = Map(e) ?? last;   // released outside the picture: let go where the pointer last was on it
            if (Button(e.GetCurrentPoint(element).Properties.PointerUpdateKind) is not { } b) return;
            element.ReleasePointerCapture(e.Pointer);
            onEvent(new RemoteInputEvent { Kind = RemoteInputKind.MouseUp, Button = b, X = f.X, Y = f.Y });
            e.Handled = true;
        }

        void Wheel(object s, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(element).Properties;
            onEvent(new RemoteInputEvent
            {
                Kind = RemoteInputKind.Wheel,
                Delta = Math.Clamp(props.MouseWheelDelta, -1200, 1200),
                Horizontal = props.IsHorizontalMouseWheel,
                X = last.X,
                Y = last.Y,
            });
            e.Handled = true;
        }

        void KeyDown(object s, KeyRoutedEventArgs e) => Key(e, RemoteInputKind.KeyDown);
        void KeyUp(object s, KeyRoutedEventArgs e) => Key(e, RemoteInputKind.KeyUp);
        void Key(KeyRoutedEventArgs e, RemoteInputKind kind)
        {
            var vk = (int)e.Key;
            if (vk is < 1 or > 254) return;
            onEvent(new RemoteInputEvent { Kind = kind, VirtualKey = vk, X = last.X, Y = last.Y });
            e.Handled = true;
        }

        PointerEventHandler moved = Moved, pressed = Pressed, released = Released, wheel = Wheel;
        KeyEventHandler down = KeyDown, up = KeyUp;
        element.PointerMoved += moved;
        element.PointerPressed += pressed;
        element.PointerReleased += released;
        element.PointerWheelChanged += wheel;
        root.PreviewKeyDown += down;
        root.PreviewKeyUp += up;

        return new Detach(() =>
        {
            element.PointerMoved -= moved;
            element.PointerPressed -= pressed;
            element.PointerReleased -= released;
            element.PointerWheelChanged -= wheel;
            root.PreviewKeyDown -= down;
            root.PreviewKeyUp -= up;
        });
    }

    private static RemoteMouseButton? Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => RemoteMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => RemoteMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => RemoteMouseButton.Middle,
        _ => null,
    };

    private sealed class Detach(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() { _undo?.Invoke(); _undo = null; }
    }
}
