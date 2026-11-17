namespace PROCTOR.Shared.Contracts;

/// <summary>What kind of answer a poll asks for.</summary>
public enum PollKind
{
    /// <summary>Two buttons: Yes / No.</summary>
    YesNo = 0,
    /// <summary>Pick one of 2–6 options.</summary>
    MultipleChoice = 1,
    /// <summary>A short written answer (exit ticket, "one word that describes…").</summary>
    ShortText = 2,
}

/// <summary>A poll a teacher puts to the whole class (P5, "polls/voting").</summary>
public sealed record Poll
{
    /// <summary>Server-assigned id.</summary>
    public string Id { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public PollKind Kind { get; init; }

    /// <summary>Answer options for <see cref="PollKind.MultipleChoice"/> (Yes/No is implied for YesNo).</summary>
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    /// <summary>When true, teachers never see who answered what (totals, and unnamed written answers).</summary>
    public bool Anonymous { get; init; }
}

/// <summary>A student's answer. Re-answering replaces the earlier answer until the poll closes.</summary>
public sealed record PollAnswer
{
    public string PollId { get; init; } = string.Empty;

    /// <summary>Index into the poll's options (YesNo: 0 = Yes, 1 = No). Ignored for ShortText.</summary>
    public int Choice { get; init; } = -1;

    /// <summary>The written answer for <see cref="PollKind.ShortText"/>.</summary>
    public string? Text { get; init; }
}

/// <summary>One student's answer as shown to teachers (absent for anonymous polls).</summary>
public sealed record PollResponse
{
    public string StudentConnectionId { get; init; } = string.Empty;
    public string Student { get; init; } = string.Empty;
    public int Choice { get; init; } = -1;
    public string? Text { get; init; }
}

/// <summary>Live results pushed to teachers whenever an answer arrives.</summary>
public sealed record PollResults
{
    public Poll Poll { get; init; } = new();

    /// <summary>Votes per option (YesNo: [yes, no]; ShortText: empty).</summary>
    public IReadOnlyList<int> Counts { get; init; } = Array.Empty<int>();

    public int Answered { get; init; }

    /// <summary>Students in the room when the results were computed.</summary>
    public int Asked { get; init; }

    public bool Closed { get; init; }

    /// <summary>Who answered what. Anonymous polls: written answers without names; choice polls: empty.</summary>
    public IReadOnlyList<PollResponse> Responses { get; init; } = Array.Empty<PollResponse>();
}

/// <summary>Poll validation shared by server (enforcement) and clients (early feedback).</summary>
public static class PollLimits
{
    public const int MaxQuestionLength = 300;
    public const int MaxOptionLength = 80;
    public const int MinOptions = 2;
    public const int MaxOptions = 6;
    public const int MaxTextAnswerLength = 200;

    /// <summary>The options a student actually chooses from.</summary>
    public static IReadOnlyList<string> EffectiveOptions(Poll poll) => poll.Kind switch
    {
        PollKind.YesNo => ["Yes", "No"],
        PollKind.MultipleChoice => poll.Options,
        _ => Array.Empty<string>(),
    };

    /// <summary>Validate and normalise a poll; returns null with a reason if it isn't usable.</summary>
    public static Poll? Clean(Poll? poll, out string? error)
    {
        error = null;
        if (poll is null || !Enum.IsDefined(poll.Kind)) { error = "Unknown poll type."; return null; }

        var question = Trim(poll.Question, MaxQuestionLength);
        if (question is null) { error = "A poll needs a question."; return null; }

        IReadOnlyList<string> options = Array.Empty<string>();
        if (poll.Kind == PollKind.MultipleChoice)
        {
            options = poll.Options.Select(o => Trim(o, MaxOptionLength)).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxOptions).ToArray();
            if (options.Count < MinOptions) { error = $"A multiple-choice poll needs {MinOptions}–{MaxOptions} different options."; return null; }
        }

        return new Poll { Id = poll.Id, Question = question, Kind = poll.Kind, Options = options, Anonymous = poll.Anonymous };
    }

    /// <summary>Validate an answer against its poll; null if it doesn't fit.</summary>
    public static PollAnswer? CleanAnswer(Poll poll, PollAnswer? answer)
    {
        if (answer is null || answer.PollId != poll.Id) return null;
        if (poll.Kind == PollKind.ShortText)
        {
            var text = Trim(answer.Text, MaxTextAnswerLength);
            return text is null ? null : new PollAnswer { PollId = poll.Id, Text = text };
        }
        var count = EffectiveOptions(poll).Count;
        return answer.Choice >= 0 && answer.Choice < count ? new PollAnswer { PollId = poll.Id, Choice = answer.Choice } : null;
    }

    private static string? Trim(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > max ? clean[..max] : clean;
    }
}
