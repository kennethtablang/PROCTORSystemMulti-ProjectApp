namespace PROCTOR.Shared.Contracts;

/// <summary>How students see the teacher's screen (FR-CTRL-02).</summary>
public enum BroadcastMode
{
    /// <summary>Covers the student's screen; students can't close it.</summary>
    FullScreen = 0,
    /// <summary>A normal window students can move, resize or minimise.</summary>
    Window = 1,
}

/// <summary>Teacher → server: show my screen to these students (empty = whole class, incl. late joiners).</summary>
public sealed record BroadcastRequest
{
    public BroadcastMode Mode { get; init; }
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set when the teacher is showing a <b>student's</b> screen (relayed through the teacher app)
    /// rather than their own. That student is never a viewer and is told their screen is on show.
    /// </summary>
    public string? SourceStudentConnectionId { get; init; }
}

/// <summary>Server → student: a teacher started showing their screen.</summary>
public sealed record BroadcastInfo
{
    public string TeacherConnectionId { get; init; } = string.Empty;
    public BroadcastMode Mode { get; init; }

    /// <summary>The shown student's computer name when it's a student's screen; null for the teacher's own.</summary>
    public string? ShowingStudentName { get; init; }
}

/// <summary>Broadcast stream parameters shared by the teacher (sender) and agents (viewers).</summary>
public static class BroadcastLimits
{
    /// <summary>
    /// Default width the teacher's screen is sent at (height follows its aspect ratio; smaller screens
    /// are sent at their native size). Full HD is the largest the managed VP8 encoder sustains at
    /// <see cref="Fps"/>: measured ≈ 71 ms/frame at 1920×1088, 123 ms at 2560×1440, 305 ms at 4K.
    /// </summary>
    public const int Width = 1920;

    /// <summary>The sharpest a teacher may choose (4K); above Full HD the frame rate drops.</summary>
    public const int MaxWidth = 3840;

    /// <summary>
    /// Resolutions the teacher can share their screen at. The app offers only those up to its own
    /// screen's width, plus that native width itself.
    /// </summary>
    public static readonly IReadOnlyList<(string Label, int Width)> Choices =
    [
        ("Standard · 1280 px", 1280),
        ("High · 1600 px", 1600),
        ("Full HD · 1920 px (recommended)", 1920),
        ("QHD · 2560 px (smoother text, fewer frames)", 2560),
        ("4K · 3840 px (sharpest, slowest)", 3840),
    ];

    public const int Fps = 10;

    /// <summary>
    /// Largest frame a viewer accepts (anything bigger is a misbehaving sender): 4K either way round,
    /// for landscape screens and portrait ones (phones, rotated monitors).
    /// </summary>
    public const int MaxFrameWidth = MaxWidth, MaxFrameHeight = MaxWidth;
}
