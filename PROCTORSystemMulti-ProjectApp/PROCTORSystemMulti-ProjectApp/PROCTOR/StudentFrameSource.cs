namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Broadcast source for "show a student's screen to the class": instead of capturing the teacher's
/// screen it re-uses the student's live stream that the teacher app is already receiving (the
/// student's <see cref="FrameBuffer"/>). Works on every teacher platform — no screen capture needed.
/// </summary>
public sealed class StudentFrameSource : IScreenSource
{
    private readonly FrameBuffer _frame;
    private long _lastVersion = -1;

    public StudentFrameSource(StudentTile tile) => _frame = tile.Frame;

    public bool IsSupported => true;

    public ScreenFrame? Capture(int maxWidth)
    {
        var f = _frame.CopyScaled(maxWidth, out var version);
        if (f is null) return null;
        var changed = version != _lastVersion;
        _lastVersion = version;
        return f.Value with { Changed = changed };
    }

    public void Dispose() { }
}
