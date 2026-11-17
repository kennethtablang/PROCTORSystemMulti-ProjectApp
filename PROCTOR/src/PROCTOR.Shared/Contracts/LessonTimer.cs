namespace PROCTOR.Shared.Contracts;

/// <summary>Teacher → server: start a countdown for the whole class.</summary>
public sealed record TimerRequest
{
    /// <summary>Optional caption, e.g. "Finish the worksheet".</summary>
    public string? Label { get; init; }

    public int Seconds { get; init; }
}

/// <summary>
/// The class countdown as pushed to students and teachers. <see cref="RemainingSeconds"/> is computed
/// when the message is sent, so each device counts down from its own clock (no clock-skew issues).
/// </summary>
public sealed record LessonTimer
{
    public bool Running { get; init; }
    public string? Label { get; init; }
    public int RemainingSeconds { get; init; }
    public int TotalSeconds { get; init; }
}

public static class TimerLimits
{
    public const int MinSeconds = 10;
    public const int MaxSeconds = 4 * 60 * 60;   // 4 hours
    public const int MaxLabelLength = 80;

    /// <summary>One printable line, capped; null if empty.</summary>
    public static string? CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var clean = new string(label.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaxLabelLength ? clean[..MaxLabelLength] : clean;
    }
}
