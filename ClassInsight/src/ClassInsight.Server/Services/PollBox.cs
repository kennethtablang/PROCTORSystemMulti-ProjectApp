using ClassInsight.Shared.Contracts;

namespace ClassInsight.Server.Services;

/// <summary>
/// The room's poll (one at a time), its answers and the live tally — in memory only, like the
/// rest of the roster (AD-6). Starting a new poll replaces the previous one. Thread-safe.
/// </summary>
public sealed class PollBox
{
    private readonly object _gate = new();
    private Poll? _poll;
    private bool _closed;

    // Latest answer per student connection, with the name shown to teachers.
    private readonly Dictionary<string, (string Student, PollAnswer Answer)> _answers = new(StringComparer.Ordinal);

    /// <summary>The open poll, or null if none is open.</summary>
    public Poll? Open { get { lock (_gate) return _closed ? null : _poll; } }

    /// <summary>The current or most recently closed poll (for a teacher who joins later).</summary>
    public Poll? Latest { get { lock (_gate) return _poll; } }

    /// <summary>Start a poll (already validated). Returns it with a fresh id.</summary>
    public Poll Start(Poll poll)
    {
        lock (_gate)
        {
            _poll = poll with { Id = Guid.NewGuid().ToString("N") };
            _closed = false;
            _answers.Clear();
            return _poll;
        }
    }

    /// <summary>Close the open poll. Returns its id, or null if nothing was open.</summary>
    public string? Close()
    {
        lock (_gate)
        {
            if (_poll is null || _closed) return null;
            _closed = true;
            return _poll.Id;
        }
    }

    /// <summary>Record (or replace) a student's answer. False if no poll is open or the answer doesn't fit.</summary>
    public bool Submit(string studentConnectionId, string studentName, PollAnswer answer)
    {
        lock (_gate)
        {
            if (_poll is null || _closed) return false;
            var clean = PollLimits.CleanAnswer(_poll, answer);
            if (clean is null) return false;
            _answers[studentConnectionId] = (studentName, clean);
            return true;
        }
    }

    /// <summary>The tally (and, unless anonymous, who answered what) for teachers.</summary>
    public PollResults? Results(int studentsInRoom)
    {
        lock (_gate)
        {
            if (_poll is null) return null;
            var options = PollLimits.EffectiveOptions(_poll);
            var counts = new int[options.Count];
            foreach (var (_, (_, a)) in _answers)
                if (a.Choice >= 0 && a.Choice < counts.Length) counts[a.Choice]++;

            return new PollResults
            {
                Poll = _poll,
                Counts = counts,
                Answered = _answers.Count,
                Asked = Math.Max(studentsInRoom, _answers.Count),
                Closed = _closed,
                Responses = _poll.Anonymous
                    // Anonymous: written answers are shown without names (sorted, so order
                    // reveals nothing); choice polls show totals only.
                    ? _poll.Kind == PollKind.ShortText
                        ? _answers.Values.Select(v => new PollResponse { Text = v.Answer.Text })
                                         .OrderBy(r => r.Text, StringComparer.OrdinalIgnoreCase).ToArray()
                        : Array.Empty<PollResponse>()
                    : _answers.Select(kv => new PollResponse
                    {
                        StudentConnectionId = kv.Key,
                        Student = kv.Value.Student,
                        Choice = kv.Value.Answer.Choice,
                        Text = kv.Value.Answer.Text,
                    }).OrderBy(r => r.Student, StringComparer.OrdinalIgnoreCase).ToArray(),
            };
        }
    }
}
