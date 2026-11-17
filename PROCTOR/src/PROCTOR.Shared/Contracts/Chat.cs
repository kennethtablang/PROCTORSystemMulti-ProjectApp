namespace PROCTOR.Shared.Contracts;

/// <summary>
/// Client → server: send a chat line. A teacher names the student (<see cref="ToConnectionId"/>);
/// a student's line always goes to the teachers, so the field is ignored for students.
/// </summary>
public sealed record ChatSend
{
    public string? ToConnectionId { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// Server → clients: one chat line. Every conversation is between the teachers and one student,
/// keyed by <see cref="StudentConnectionId"/>. Relayed only — the server keeps no chat history (AD-6).
/// </summary>
public sealed record ChatMessage
{
    public string Id { get; init; } = string.Empty;
    public string StudentConnectionId { get; init; } = string.Empty;
    public bool FromTeacher { get; init; }
    public string FromName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset SentAt { get; init; }
}

public static class ChatLimits
{
    /// <summary>Messages a student may send per <see cref="Window"/> (teachers get more).</summary>
    public const int StudentBurst = 6;
    public const int TeacherBurst = 30;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>Lines kept per conversation on screen (teacher app and student window).</summary>
    public const int MaxHistory = 200;
}
