namespace PROCTOR.Shared.Contracts;

public enum RemoteInputKind
{
    MouseMove = 0,
    MouseDown = 1,
    MouseUp = 2,
    Wheel = 3,
    KeyDown = 4,
    KeyUp = 5,
}

public enum RemoteMouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
}

/// <summary>
/// One input event from the teacher's focus view. Mouse positions are normalised (0–1) across the
/// streamed display, so they're independent of the teacher's window size and the stream resolution.
/// Keys are Windows virtual-key codes.
/// </summary>
public sealed record RemoteInputEvent
{
    public RemoteInputKind Kind { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public RemoteMouseButton Button { get; init; }

    /// <summary>Wheel delta (120 = one notch); <see cref="Horizontal"/> for tilt wheels.</summary>
    public int Delta { get; init; }
    public bool Horizontal { get; init; }

    public int VirtualKey { get; init; }
}

/// <summary>Teacher → server → student: a batch of input for the display being controlled.</summary>
public sealed record RemoteInputBatch
{
    public string StudentConnectionId { get; init; } = string.Empty;
    public int DisplayIndex { get; init; }
    public IReadOnlyList<RemoteInputEvent> Events { get; init; } = Array.Empty<RemoteInputEvent>();
}

/// <summary>Server → student: a teacher took (or released) control of this PC.</summary>
public sealed record RemoteControlInfo
{
    public string TeacherConnectionId { get; init; } = string.Empty;
    public bool Active { get; init; }
}

public static class RemoteControlLimits
{
    public const int MaxEventsPerBatch = 64;

    /// <summary>Batches a controlling teacher may send per second (the app sends ~30).</summary>
    public const int MaxBatchesPerSecond = 120;

    /// <summary>How often the teacher app flushes queued input.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>A valid event: known kind/button, position inside the display, sane wheel and key values.</summary>
    public static bool IsValid(RemoteInputEvent e) => e is not null
        && Enum.IsDefined(e.Kind) && Enum.IsDefined(e.Button)
        && e.X is >= 0 and <= 1 && e.Y is >= 0 and <= 1
        && e.Delta is >= -1200 and <= 1200
        && (e.Kind is not (RemoteInputKind.KeyDown or RemoteInputKind.KeyUp) || e.VirtualKey is >= 1 and <= 254);
}
