using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// A SkiaSharp surface that paints a student's live <see cref="FrameBuffer"/> (letterboxed) and
/// repaints whenever a new frame is decoded. Used for every dashboard thumbnail and the focus
/// view, on all four platforms (one Skia path, NFR-PORT-01). Repaints are coalesced so a burst
/// of frames costs at most one pending UI-thread invalidate per view.
/// </summary>
public sealed class LiveScreenView : SKCanvasView
{
    public static readonly BindableProperty SourceProperty = BindableProperty.Create(
        nameof(Source), typeof(FrameBuffer), typeof(LiveScreenView),
        propertyChanged: (b, o, n) => ((LiveScreenView)b).OnFrameChanged(o as FrameBuffer, n as FrameBuffer));

    private int _pending;
    private bool _attached;

    public LiveScreenView()
    {
        IgnorePixelScaling = false;
        BackgroundColor = Color.FromArgb("#111111");
    }

    /// <summary>The frame source to paint (bind to <c>StudentTile.Frame</c>).</summary>
    public FrameBuffer? Source
    {
        get => (FrameBuffer?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private void OnFrameChanged(FrameBuffer? old, FrameBuffer? now)
    {
        if (old is not null) old.Updated -= OnFrameUpdated;
        if (now is not null && _attached) now.Updated += OnFrameUpdated;
        InvalidateSurface();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        var attach = Handler is not null;
        if (attach == _attached) return;
        _attached = attach;

        // Only listen while on screen, so recycled/off-screen views don't hold the tile alive or
        // queue repaints.
        if (Source is { } f)
        {
            if (attach) f.Updated += OnFrameUpdated;
            else f.Updated -= OnFrameUpdated;
        }
        if (attach) InvalidateSurface();
    }

    private void OnFrameUpdated()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.Dispatch(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            InvalidateSurface();
        });
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x11, 0x11, 0x11));
        Source?.Draw(canvas, new SKRect(0, 0, e.Info.Width, e.Info.Height));
    }
}
