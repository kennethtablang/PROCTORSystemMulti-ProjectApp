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
    /// <summary>Width the teacher's screen is sent at (height follows its aspect ratio).</summary>
    public const int Width = 1280;

    public const int Fps = 10;

    /// <summary>Largest frame a viewer accepts (anything bigger is a misbehaving sender).</summary>
    public const int MaxFrameWidth = 1920, MaxFrameHeight = 2160;
}
