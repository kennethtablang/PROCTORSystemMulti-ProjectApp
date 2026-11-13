using System.Runtime.InteropServices;
using ClassInsight.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.ClassInsight;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Focus view: a single student's screen streamed live over WebRTC and painted with
/// SkiaSharp. Owns a <see cref="ScreenStreamReceiver"/> for the lifetime of the page and
/// tears it down on navigation away so the agent can stop encoding (FR-STREAM-06).
/// </summary>
public partial class FocusPage : ContentPage
{
    private readonly StudentTile _tile;
    private readonly TeacherClient _client;

    private readonly object _frameLock = new();
    private SKBitmap? _bitmap;         // latest frame, owned by the UI
    private ScreenStreamReceiver? _receiver;

    public FocusPage(StudentTile tile, TeacherClient client)
    {
        InitializeComponent();
        _tile = tile;
        _client = client;

        Title = tile.Header;
        HeaderLabel.Text = tile.Header;
        StatusLabel.Text = "Starting…";

        // The detail panel binds to the live tile, which the dashboard keeps updating from
        // the telemetry stream while this page is open — so CPU/RAM/bandwidth/processes
        // refresh in place (FR-TEL-03).
        DetailPanel.BindingContext = tile;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var hub = _client.Hub;
        if (hub is null)
        {
            StatusLabel.Text = "Not connected to a server.";
            return;
        }

        var receiver = new ScreenStreamReceiver(hub, _tile.ConnectionId, StreamQuality.Focus);
        receiver.Status += OnStatus;
        receiver.FrameDecoded += OnFrameDecoded;
        _receiver = receiver;

        try
        {
            await receiver.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            OnStatus($"Could not start stream: {ex.Message}");
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await TeardownAsync();
    }

    private async void OnStopClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

    private async Task TeardownAsync()
    {
        var receiver = _receiver;
        _receiver = null;
        if (receiver is not null)
        {
            receiver.Status -= OnStatus;
            receiver.FrameDecoded -= OnFrameDecoded;
            await receiver.DisposeAsync();
        }

        lock (_frameLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }

    private void OnStatus(string status) =>
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = status);

    private void OnFrameDecoded(DecodedFrame frame)
    {
        // Runs on a WebRTC thread; frame.Bgra is a reused buffer, so copy it into our own
        // SKBitmap immediately, then ask the canvas to repaint on the UI thread.
        lock (_frameLock)
        {
            if (_bitmap is null || _bitmap.Width != frame.Width || _bitmap.Height != frame.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height,
                    SKColorType.Bgra8888, SKAlphaType.Opaque));
            }

            var pixels = _bitmap.GetPixels();
            var bytes = frame.Width * frame.Height * 4;
            Marshal.Copy(frame.Bgra, 0, pixels, bytes);
        }

        MainThread.BeginInvokeOnMainThread(() => Canvas.InvalidateSurface());
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);

        lock (_frameLock)
        {
            if (_bitmap is null) return;

            // Letterbox the frame into the surface, preserving aspect ratio.
            var info = e.Info;
            float scale = Math.Min((float)info.Width / _bitmap.Width, (float)info.Height / _bitmap.Height);
            float w = _bitmap.Width * scale, h = _bitmap.Height * scale;
            float x = (info.Width - w) / 2f, y = (info.Height - h) / 2f;

            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(_bitmap, new SKRect(x, y, x + w, y + h), paint);
        }
    }
}
