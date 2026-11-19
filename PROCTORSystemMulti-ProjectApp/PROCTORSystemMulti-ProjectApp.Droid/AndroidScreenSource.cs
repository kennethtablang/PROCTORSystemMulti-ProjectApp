using System.Runtime.InteropServices;
using Android.App;
using Android.Content;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.Runtime;
using Android.Util;
using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp.Droid;

/// <summary>
/// Captures the Android teacher's screen for the class broadcast (FR-CTRL-02), like the Windows
/// app's "show my screen". <see cref="PrepareAsync"/> asks Android for screen-recording consent
/// (the system "Start now" prompt), starts <see cref="ScreenCaptureService"/> (required for
/// recording; it shows a notification) and mirrors the screen into an <see cref="ImageReader"/>.
/// <see cref="Capture"/> converts the newest image to the BGRA frame the VP8 encoder takes.
///
/// The picture is sized once, when sharing starts (Android allows one virtual display per consent):
/// the long side is capped at the resolution the teacher chose (Settings; a phone CPU encodes
/// 1280 px comfortably, larger sizes send fewer frames).
/// </summary>
public sealed class AndroidScreenSource : IScreenSource
{
    private const int RequestCode = 0x5C2E;
    private int _maxLongSide = 1280;

    /// <summary>The screen's longer side in pixels.</summary>
    public int NativeWidth
    {
        get
        {
            try
            {
                var metrics = new DisplayMetrics();
#pragma warning disable CA1422, CS0618
                (Platform.CurrentActivity?.WindowManager?.DefaultDisplay)?.GetRealMetrics(metrics);
#pragma warning restore CA1422, CS0618
                return Math.Max(metrics.WidthPixels, metrics.HeightPixels);
            }
            catch { return 0; }
        }
    }

    private readonly object _gate = new();
    private MediaProjection? _projection;
    private ProjectionCallback? _callback;
    private VirtualDisplay? _display;
    private ImageReader? _reader;
    private int _w, _h;
    private byte[] _current = [], _previous = [], _row = [];
    private bool _have;

    public bool IsSupported => OperatingSystem.IsAndroidVersionAtLeast(21);

    public async Task<string?> PrepareAsync(int maxWidth)
    {
        Release();
        _maxLongSide = Math.Clamp(maxWidth, 320, BroadcastLimits.MaxWidth);
        if (Platform.CurrentActivity is not { } activity) return "The app isn't ready yet. Try again.";
        if (activity.GetSystemService(Context.MediaProjectionService) is not MediaProjectionManager manager)
            return "This device can't share its screen.";

        // 1. The teacher allows recording (Android's own prompt; it can't be skipped or pre-approved).
        var consent = new TaskCompletionSource<(Result Code, Intent? Data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResult(int request, Result code, Intent? data)
        {
            if (request == RequestCode) consent.TrySetResult((code, data));
        }
        MainActivity.ActivityResult += OnResult;
        try
        {
            activity.StartActivityForResult(manager.CreateScreenCaptureIntent(), RequestCode);
            var (code, data) = await consent.Task;
            if (code != Result.Ok || data is null) return "Screen sharing wasn't allowed. Tap \"Start now\" when Android asks.";

            // 2. The foreground service turns the consent into a projection (Android 10+ requires it).
            var started = new TaskCompletionSource<MediaProjection?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStarted(MediaProjection? p) => started.TrySetResult(p);
            ScreenCaptureService.ProjectionStarted += OnStarted;
            try
            {
                var service = new Intent(activity, typeof(ScreenCaptureService))
                    .PutExtra(ScreenCaptureService.ExtraResultCode, (int)code)
                    .PutExtra(ScreenCaptureService.ExtraResultData, data);
                if (OperatingSystem.IsAndroidVersionAtLeast(26)) activity.StartForegroundService(service);
                else activity.StartService(service);

                var projection = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (projection is null) { StopService(); return "Android didn't start screen sharing. Try again."; }

                // 3. Mirror the screen into an image reader at the broadcast size.
                StartMirroring(activity, projection);
                return null;
            }
            catch (TimeoutException)
            {
                StopService();
                return "Android didn't start screen sharing. Try again.";
            }
            finally
            {
                ScreenCaptureService.ProjectionStarted -= OnStarted;
            }
        }
        catch (Exception ex)
        {
            Release();
            return "Screen sharing couldn't start: " + ex.Message;
        }
        finally
        {
            MainActivity.ActivityResult -= OnResult;
        }
    }

    private void StartMirroring(Activity activity, MediaProjection projection)
    {
        var metrics = new DisplayMetrics();
#pragma warning disable CA1422, CS0618 // GetRealMetrics: still the real panel size on every version
        activity.WindowManager?.DefaultDisplay?.GetRealMetrics(metrics);
#pragma warning restore CA1422, CS0618
        int sw = Math.Max(16, metrics.WidthPixels), sh = Math.Max(16, metrics.HeightPixels);
        var scale = Math.Min(1.0, (double)_maxLongSide / Math.Max(sw, sh));
        static int Mb(double v) => Math.Max(16, (int)Math.Round(v / 16.0) * 16);
        int w = Mb(sw * scale), h = Mb(sh * scale);

        lock (_gate)
        {
            _projection = projection;
            // Android 14+: a callback must be registered before the virtual display is created.
            _callback = new ProjectionCallback(this);
            projection.RegisterCallback(_callback, null);
            _reader = ImageReader.NewInstance(w, h, (Android.Graphics.ImageFormatType)1 /* PixelFormat.RGBA_8888 */, 2);
            _display = projection.CreateVirtualDisplay("PROCTOR screen", w, h, Math.Max(1, (int)metrics.DensityDpi),
                (Android.Views.DisplayFlags)VirtualDisplayFlags.AutoMirror, _reader.Surface, null, null);
            _w = w; _h = h; _have = false;
        }
    }

    public ScreenFrame? Capture(int maxWidth)
    {
        lock (_gate)
        {
            if (_reader is null) return null;
            using var image = _reader.AcquireLatestImage();
            if (image is null)
                // Nothing new on screen: Android only produces frames when it changes.
                return _have ? new ScreenFrame(_w, _h, _previous, false) : null;

            var planes = image.GetPlanes();
            if (planes is not { Length: > 0 } || planes[0].Buffer is not { } buffer) return null;
            var address = buffer.GetDirectBufferAddress();
            if (address == IntPtr.Zero) return null;

            int w = Math.Min(_w, image.Width), h = Math.Min(_h, image.Height);
            int rowStride = planes[0].RowStride, pixelStride = planes[0].PixelStride;
            if (pixelStride != 4) return null;

            var len = _w * _h * 4;
            if (_current.Length != len) { _current = new byte[len]; _previous = new byte[len]; _have = false; }
            if (_row.Length < rowStride) _row = new byte[rowStride];

            // RGBA rows (padded to rowStride) -> tightly packed BGRA.
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(address + y * rowStride, _row, 0, Math.Min(rowStride, w * 4));
                var o = y * _w * 4;
                for (var x = 0; x < w; x++)
                {
                    var i = x * 4;
                    _current[o + i] = _row[i + 2];
                    _current[o + i + 1] = _row[i + 1];
                    _current[o + i + 2] = _row[i];
                    _current[o + i + 3] = 255;
                }
            }

            var changed = !_have || !_current.AsSpan().SequenceEqual(_previous);
            (_current, _previous) = (_previous, _current);
            _have = true;
            return new ScreenFrame(_w, _h, _previous, changed);
        }
    }

    public void Release()
    {
        MediaProjection? projection;
        lock (_gate)
        {
            projection = _projection;
            _projection = null;
            try { _display?.Release(); } catch { }
            _display = null;
            try { _reader?.Close(); } catch { }
            _reader = null;
            if (projection is not null && _callback is not null)
                try { projection.UnregisterCallback(_callback); } catch { }
            _callback = null;
            _have = false;
        }
        try { projection?.Stop(); } catch { }
        StopService();
    }

    private static void StopService()
    {
        try
        {
            var context = Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(ScreenCaptureService)));
        }
        catch { }
    }

    public void Dispose() => Release();

    /// <summary>The teacher stopped sharing from Android itself (status bar chip / notification).</summary>
    private sealed class ProjectionCallback(AndroidScreenSource owner) : MediaProjection.Callback
    {
        public override void OnStop()
        {
            lock (owner._gate)
            {
                try { owner._display?.Release(); } catch { }
                owner._display = null;
            }
        }
    }
}
