namespace PROCTOR.Shared.Contracts;

/// <summary>Teacher → server → students: please type your name (attendance).</summary>
public sealed record AttendanceRequest
{
    /// <summary>Set by the server; changes with every new request (a new class).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Optional class name shown on the form, e.g. "IT 101 — Section A".</summary>
    public string? Title { get; init; }

    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>Student → server: the name typed on the attendance form.</summary>
public sealed record AttendanceSubmission
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string? MiddleInitial { get; init; }
}

/// <summary>A student's name as submitted (validated by the server).</summary>
public sealed record StudentName
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;

    /// <summary>One or two letters, without the period ("M"), or null.</summary>
    public string? MiddleInitial { get; init; }

    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>"Dela Cruz, Juan M." — for lists sorted by surname.</summary>
    public string Display => $"{LastName}, {FirstName}{(MiddleInitial is { Length: > 0 } m ? $" {m}." : "")}";

    /// <summary>"Juan M. Dela Cruz".</summary>
    public string FullName => $"{FirstName}{(MiddleInitial is { Length: > 0 } m ? $" {m}." : "")} {LastName}";
}

/// <summary>Server → teachers: one student's name changed (null = cleared).</summary>
public sealed record StudentNameUpdate
{
    public string StudentConnectionId { get; init; } = string.Empty;
    public StudentName? Name { get; init; }
}

/// <summary>Server → teachers: is the class being asked for names right now.</summary>
public sealed record AttendanceStatus
{
    /// <summary>The open request, or null when students aren't being asked.</summary>
    public AttendanceRequest? Open { get; init; }

    /// <summary>Names the server holds (PCs that have answered).</summary>
    public int Named { get; init; }
}

public static class AttendanceLimits
{
    public const int MaxNameLength = 40;
    public const int MaxTitleLength = 80;

    /// <summary>Validate a submission; returns the clean name, or null with a message for the student.</summary>
    public static StudentName? Clean(AttendanceSubmission? s, DateTimeOffset now, out string? error)
    {
        error = null;
        var first = CleanPart(s?.FirstName);
        var last = CleanPart(s?.LastName);
        var mi = new string((s?.MiddleInitial ?? "").Where(char.IsLetter).ToArray());
        if (first is null) { error = "Type your first name."; return null; }
        if (last is null) { error = "Type your last name."; return null; }
        if (mi.Length > 2) { error = "The middle initial is one or two letters."; return null; }
        return new StudentName
        {
            FirstName = first,
            LastName = last,
            MiddleInitial = mi.Length == 0 ? null : mi.ToUpperInvariant(),
            SubmittedAt = now,
        };
    }

    /// <summary>A class title for the form, trimmed and capped; null if empty.</summary>
    public static string? CleanTitle(string? title)
    {
        var t = new string((title ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length == 0 ? null : t.Length > MaxTitleLength ? t[..MaxTitleLength].Trim() : t;
    }

    /// <summary>
    /// A name part: letters (any language, so "Ñ" and accents are fine), spaces, hyphens, apostrophes
    /// and periods ("Ma. Teresa", "Dela Cruz", "O'Brien"). Each word gets a capital first letter if
    /// typed all in lower case. Null if nothing valid is left or something else was typed.
    /// </summary>
    public static string? CleanPart(string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        var text = string.Join(' ', part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (text.Length is 0 or > MaxNameLength) return null;
        if (!text.All(c => char.IsLetter(c) || c is ' ' or '-' or '\'' or '.' or '’')) return null;
        if (!text.Any(char.IsLetter)) return null;
        if (text == text.ToLowerInvariant())
            text = string.Join(' ', text.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return text;
    }
}
