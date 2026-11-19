using System.Runtime.InteropServices;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp.WinUI;

/// <summary>
/// Captures the Windows teacher's primary screen for the class broadcast (FR-CTRL-02): one GDI
/// HALFTONE <c>StretchBlt</c> from the screen into a reused DIB at the target size (both sides
/// multiples of 16 for the VP8 encoder), with the mouse pointer drawn on top so students can follow
/// what the teacher points at. Two buffers alternate so unchanged frames can be skipped.
/// </summary>
public sealed class WindowsScreenSource : IScreenSource
{
    private IntPtr _memDc, _dib, _old, _bits;
    private int _w, _h;
    private byte[] _current = [], _previous = [];
    private bool _have;

    public bool IsSupported => true;

    /// <summary>The primary screen's width in physical pixels (the app is per-monitor DPI aware).</summary>
    public int NativeWidth => Math.Max(0, GetSystemMetrics(SM_CXSCREEN));

    public ScreenFrame? Capture(int maxWidth)
    {
        int sw = Math.Max(1, GetSystemMetrics(SM_CXSCREEN)), sh = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
        var scale = Math.Min(1.0, (double)maxWidth / sw);
        static int Mb(double v) => Math.Max(16, (int)Math.Round(v / 16.0) * 16);
        int tw = Math.Min(Mb(sw * scale), Math.Max(16, maxWidth / 16 * 16)), th = Mb(sh * scale);
        if (!Ensure(tw, th)) return null;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            SetStretchBltMode(_memDc, HALFTONE);
            SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
            if (!StretchBlt(_memDc, 0, 0, tw, th, screen, 0, 0, sw, sh, SRCCOPY | CAPTUREBLT)) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }

        DrawCursor(tw, sw, sh);
        GdiFlush();

        var len = tw * th * 4;
        if (_current.Length != len) { _current = new byte[len]; _previous = new byte[len]; _have = false; }
        Marshal.Copy(_bits, _current, 0, len);
        var changed = !_have || !_current.AsSpan().SequenceEqual(_previous);
        (_current, _previous) = (_previous, _current);
        _have = true;
        return new ScreenFrame(tw, th, _previous, changed);
    }

    private void DrawCursor(int tw, int sw, int sh)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
            if (ci.x < 0 || ci.y < 0 || ci.x >= sw || ci.y >= sh) return;
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out var info))
            {
                hx = info.xHotspot; hy = info.yHotspot;
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            }
            var scale = (double)tw / sw;
            var size = Math.Max(16, (int)Math.Round(32 * scale));
            var cs = size / 32.0;
            DrawIconEx(_memDc, (int)Math.Round(ci.x * scale - hx * cs), (int)Math.Round(ci.y * scale - hy * cs), ci.hCursor, size, size, 0, IntPtr.Zero, DI_NORMAL);
        }
        catch { }
    }

    private bool Ensure(int w, int h)
    {
        if (_dib != IntPtr.Zero && _w == w && _h == h) return true;
        Free();
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;
        try
        {
            _memDc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            _dib = CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero || _bits == IntPtr.Zero) { Free(); return false; }
            _old = SelectObject(_memDc, _dib);
            _w = w; _h = h;
            return true;
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    private void Free()
    {
        if (_memDc != IntPtr.Zero && _old != IntPtr.Zero) SelectObject(_memDc, _old);
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
        _memDc = _dib = _old = _bits = IntPtr.Zero;
        _w = _h = 0;
    }

    public void Dispose() => Free();

    // ---- interop ----
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, HALFTONE = 4;
    private const int CURSOR_SHOWING = 1, DI_NORMAL = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { [MarshalAs(UnmanagedType.Bool)] public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int m);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, int rop);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
}
