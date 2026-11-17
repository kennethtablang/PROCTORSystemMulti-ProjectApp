using PROCTOR.Shared.Contracts;

namespace PROCTOR.Server.Services;

/// <summary>
/// The room's poll (one at a time), its answers and the live tally — in memory only, like the
/// rest of the roster (AD-6). Starting a new poll replaces the previous one. Thread-safe.
///
/// Integrity: answers are keyed by the student PC's install id (<see cref="StudentInfo.MachineId"/>),
/// not the connection, so a PC that reconnects counts once; an answer is dropped when its student
/// leaves the room, so the tally only ever reflects students currently on the roster (each a visible
/// tile on the teacher's dashboard). Students join without a login by design, so this can't stop a
/// rogue device posing as extra students — but every counted answer belongs to a student the
/// teacher can see.
/// </summary>
public sealed class PollBox
{
    private readonly object _gate = new();
    private Poll? _poll;
    private bool _closed;

    // Latest answer per student PC (MachineId), with its current connection and display name.
    private readonly Dictionary<string, (string Connection, string Student, PollAnswer Answer)> _answers = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Record (or replace) a student's answer, keyed by their PC's <paramref name="studentKey"/>.
    /// False if no poll is open or the answer doesn't fit.
    /// </summary>
    public bool Submit(string studentKey, string connectionId, string studentName, PollAnswer answer)
    {
        lock (_gate)
        {
            if (_poll is null || _closed) return false;
            var clean = PollLimits.CleanAnswer(_poll, answer);
            if (clean is null) return false;
            // One answer per connection, whatever key it arrives under.
            foreach (var stale in _answers.Where(kv => kv.Value.Connection == connectionId && kv.Key != studentKey)
                                          .Select(kv => kv.Key).ToArray())
                _answers.Remove(stale);
            _answers[studentKey] = (connectionId, studentName, clean);
            return true;
        }
    }

    /// <summary>A student left the room: their answer no longer counts (while the poll is open).</summary>
    public bool RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (_poll is null || _closed) return false;
            var keys = _answers.Where(kv => kv.Value.Connection == connectionId).Select(kv => kv.Key).ToArray();
            foreach (var k in keys) _answers.Remove(k);
            return keys.Length > 0;
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
            foreach (var (_, (_, _, a)) in _answers)
                if (a.Choice >= 0 && a.Choice < counts.Length) counts[a.Choice]++;

            return new PollResults
            {
                Poll = _poll,
                Counts = counts,
                // Never more answers than students: a closed poll keeps its final tally even if
                // students have since left, so cap only while open.
                Answered = _closed ? _answers.Count : Math.Min(_answers.Count, studentsInRoom),
                Asked = _closed ? Math.Max(studentsInRoom, _answers.Count) : studentsInRoom,
                Closed = _closed,
                Responses = _poll.Anonymous
                    // Anonymous: written answers are shown without names (sorted, so order
                    // reveals nothing); choice polls show totals only.
                    ? _poll.Kind == PollKind.ShortText
                        ? _answers.Values.Select(v => new PollResponse { Text = v.Answer.Text })
                                         .OrderBy(r => r.Text, StringComparer.OrdinalIgnoreCase).ToArray()
                        : Array.Empty<PollResponse>()
                    : _answers.Values.Select(v => new PollResponse
                    {
                        StudentConnectionId = v.Connection,
                        Student = v.Student,
                        Choice = v.Answer.Choice,
                        Text = v.Answer.Text,
                    }).OrderBy(r => r.Student, StringComparer.OrdinalIgnoreCase).ToArray(),
            };
        }
    }
}
