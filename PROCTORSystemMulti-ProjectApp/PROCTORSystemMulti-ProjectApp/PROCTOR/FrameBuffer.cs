using SkiaSharp;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// The latest decoded video frame for one student, shared between the WebRTC decode thread
/// (writer) and any number of on-screen <see cref="LiveScreenView"/>s (readers): the dashboard
/// thumbnail and the focus view draw the same buffer. Writes convert the decoder's BGR output
/// straight into a reused Skia bitmap — no per-frame allocation.
/// </summary>
public sealed class FrameBuffer
{
    private readonly object _lock = new();
    private SKBitmap? _bitmap;

    /// <summary>Raised (on the decode thread) after a new frame has been written.</summary>
    public event Action? Updated;

    public bool HasFrame { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    private long _version;

    /// <summary>Copy a 24-bpp BGR frame (top-down, tightly packed) in as opaque BGRA.</summary>
    public void WriteBgr(int width, int height, ReadOnlySpan<byte> bgr)
    {
        if (width <= 0 || height <= 0 || bgr.Length < (long)width * height * 3) return;

        lock (_lock)
        {
            if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            }

            var dst = _bitmap.GetPixelSpan();
            var pixels = width * height;
            for (int i = 0, s = 0, d = 0; i < pixels; i++, s += 3, d += 4)
            {
                dst[d] = bgr[s];
                dst[d + 1] = bgr[s + 1];
                dst[d + 2] = bgr[s + 2];
                dst[d + 3] = 0xFF;
            }
            _bitmap.NotifyPixelsChanged();
            _version++;

            Width = width;
            Height = height;
            HasFrame = true;
        }

        Updated?.Invoke();
    }

    /// <summary>Forget the current frame (stream stopped), so views fall back to their placeholder.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            HasFrame = false;
            Width = Height = 0;
        }
        Updated?.Invoke();
    }

    /// <summary>
    /// The current frame as BGRA scaled to about <paramref name="maxWidth"/> (sides rounded down to
    /// multiples of 16 for the VP8 encoder), for re-broadcasting; null if there's no frame yet.
    /// <paramref name="version"/> changes whenever a new frame arrives.
    /// </summary>
    public ScreenFrame? CopyScaled(int maxWidth, out long version)
    {
        lock (_lock)
        {
            version = _version;
            if (_bitmap is null) return null;
            var scale = Math.Min(1.0, (double)maxWidth / _bitmap.Width);
            var w = Math.Max(16, (int)(_bitmap.Width * scale) / 16 * 16);
            var h = Math.Max(16, (int)(_bitmap.Height * scale) / 16 * 16);
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var scaled = new SKBitmap(info);
            if (!_bitmap.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))) return null;
            var bytes = scaled.GetPixelSpan().ToArray();
            return new ScreenFrame(w, h, bytes, true);
        }
    }

    /// <summary>The current frame as PNG bytes (for screenshots), or null if there's no frame yet.</summary>
    public byte[]? EncodePng()
    {
        lock (_lock)
        {
            if (_bitmap is null) return null;
            using var image = SKImage.FromBitmap(_bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
    }

    /// <summary>Draw the frame letterboxed into <paramref name="bounds"/>. Returns false if there is no frame yet.</summary>
    public bool Draw(SKCanvas canvas, SKRect bounds)
    {
        lock (_lock)
        {
            if (_bitmap is null) return false;

            var scale = Math.Min(bounds.Width / _bitmap.Width, bounds.Height / _bitmap.Height);
            var w = _bitmap.Width * scale;
            var h = _bitmap.Height * scale;
            var x = bounds.Left + (bounds.Width - w) / 2f;
            var y = bounds.Top + (bounds.Height - h) / 2f;

            using var image = SKImage.FromBitmap(_bitmap);
            canvas.DrawImage(image, new SKRect(x, y, x + w, y + h),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            return true;
        }
    }
}
