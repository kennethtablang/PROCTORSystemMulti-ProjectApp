namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>A captured teacher screen frame: BGRA, top-down, stride = Width*4, sides multiples of 16.</summary>
public readonly record struct ScreenFrame(int Width, int Height, byte[] Bgra, bool Changed);

/// <summary>
/// Captures the teacher's own screen for broadcasting it to the class (FR-CTRL-02). Implemented per
/// platform by the app heads (Windows today); other heads use <see cref="UnsupportedScreenSource"/>.
/// </summary>
public interface IScreenSource : IDisposable
{
    bool IsSupported { get; }

    /// <summary>Grab the primary screen scaled to about <paramref name="maxWidth"/>; null if capture failed this time.</summary>
    ScreenFrame? Capture(int maxWidth);
}

/// <summary>Heads that can't capture their screen yet (Android, iOS, Mac Catalyst).</summary>
public sealed class UnsupportedScreenSource : IScreenSource
{
    public bool IsSupported => false;
    public ScreenFrame? Capture(int maxWidth) => null;
    public void Dispose() { }
}
