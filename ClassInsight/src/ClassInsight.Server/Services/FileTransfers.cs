using ClassInsight.Shared.Contracts;

namespace ClassInsight.Server.Services;

/// <summary>
/// Bookkeeping for file transfers relayed through the hub (the bytes are never stored): who may
/// send chunks for which transfer, to whom, in what order and up to what size; who may report a
/// result back; and which teacher each student may hand work in to. Thread-safe, in memory only.
/// </summary>
public sealed class FileTransfers
{
    private sealed class Transfer
    {
        public required string Id { get; init; }
        public required string Sender { get; init; }
        public required HashSet<string> Targets { get; init; }
        public required long Size { get; init; }
        public long Received;
        public int NextIndex;
        public bool Finished;
        public DateTimeOffset LastActivity;
        public readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    }

    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _handInTeacher = new(StringComparer.Ordinal);   // student → teacher
    private readonly Dictionary<string, int> _handInCount = new(StringComparer.Ordinal);

    public FileTransfers() : this(TimeProvider.System) { }
    public FileTransfers(TimeProvider clock) => _clock = clock;

    /// <summary>Register a new transfer; null if the sender already has too many open.</summary>
    public string? Begin(string sender, IEnumerable<string> targets, long size)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var stale in _transfers.Values.Where(t => now - t.LastActivity > Expiry).Select(t => t.Id).ToList())
                _transfers.Remove(stale);
            if (_transfers.Values.Count(t => t.Sender == sender && !t.Finished) >= FileLimits.MaxOpenTransfersPerSender) return null;
            var id = Guid.NewGuid().ToString("N");
            _transfers[id] = new Transfer
            {
                Id = id,
                Sender = sender,
                Targets = new HashSet<string>(targets, StringComparer.Ordinal),
                Size = size,
                LastActivity = now,
            };
            return id;
        }
    }

    /// <summary>Accept the next chunk from its sender; returns the targets to relay to, or null to refuse.</summary>
    public string[]? Chunk(string sender, FileChunk chunk)
    {
        lock (_gate)
        {
            if (chunk?.Data is null || !_transfers.TryGetValue(chunk.TransferId ?? "", out var t) || t.Sender != sender || t.Finished) return null;
            if (chunk.Index != t.NextIndex || chunk.Data.Length == 0 || chunk.Data.Length > FileLimits.ChunkSize
                || t.Received + chunk.Data.Length > t.Size) return null;
            t.NextIndex++;
            t.Received += chunk.Data.Length;
            t.LastActivity = _clock.GetUtcNow();
            return t.Targets.ToArray();
        }
    }

    /// <summary>All bytes arrived: returns the targets to tell, or null if incomplete / not the sender's.</summary>
    public string[]? Finish(string sender, string transferId)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId ?? "", out var t) || t.Sender != sender || t.Finished || t.Received != t.Size) return null;
            t.Finished = true;
            t.LastActivity = _clock.GetUtcNow();
            return t.Targets.ToArray();
        }
    }

    /// <summary>The sender gives up: returns the targets to tell, or null.</summary>
    public string[]? Cancel(string sender, string transferId)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId ?? "", out var t) || t.Sender != sender) return null;
            _transfers.Remove(t.Id);
            return t.Targets.ToArray();
        }
    }

    /// <summary>A receiver reports back once; returns the sender to tell, or null.</summary>
    public string? Result(string reporter, string transferId)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId ?? "", out var t) || !t.Targets.Contains(reporter) || !t.Reported.Add(reporter)) return null;
            if (t.Finished && t.Reported.IsSupersetOf(t.Targets)) _transfers.Remove(t.Id);
            return t.Sender;
        }
    }

    // ---- hand-in ------------------------------------------------------------

    /// <summary>
    /// Start collecting from these students. A student another (still connected) teacher is already
    /// collecting from is skipped, so one teacher can't redirect another's hand-ins. Returns
    /// (opened, skipped).
    /// </summary>
    public (string[] Opened, int Skipped) OpenHandIn(string teacher, IEnumerable<string> students, Func<string, bool> isConnectedTeacher)
    {
        lock (_gate)
        {
            var opened = new List<string>();
            var skipped = 0;
            foreach (var s in students)
            {
                _handInTeacher.TryGetValue(s, out var owner);
                if (owner is not null && owner != teacher && isConnectedTeacher(owner))
                {
                    skipped++;
                    continue;
                }
                if (owner != teacher) _handInCount[s] = 0;   // a teacher re-asking doesn't reset its own count
                _handInTeacher[s] = teacher;
                opened.Add(s);
            }
            return (opened.ToArray(), skipped);
        }
    }

    /// <summary>Stop <paramref name="teacher"/>'s collection; returns the students it was open for.</summary>
    public string[] CloseHandIn(string teacher)
    {
        lock (_gate)
        {
            var students = _handInTeacher.Where(kv => kv.Value == teacher).Select(kv => kv.Key).ToArray();
            foreach (var s in students) { _handInTeacher.Remove(s); _handInCount.Remove(s); }
            return students;
        }
    }

    /// <summary>The teacher this student may hand a file in to (counting it), or null if none / over the limit.</summary>
    public string? ReserveHandIn(string student)
    {
        lock (_gate)
        {
            if (!_handInTeacher.TryGetValue(student, out var teacher)) return null;
            var n = _handInCount.GetValueOrDefault(student);
            if (n >= FileLimits.MaxHandInFilesPerStudent) return null;
            _handInCount[student] = n + 1;
            return teacher;
        }
    }

    public bool IsCollecting(string student)
    {
        lock (_gate) return _handInTeacher.ContainsKey(student);
    }

    /// <summary>
    /// A connection left. Returns its unfinished transfers (with their targets) so they can be
    /// cancelled; it's also dropped as a target and from hand-in.
    /// </summary>
    public List<(string Id, string[] Targets)> RemoveConnection(string id)
    {
        lock (_gate)
        {
            var cancelled = new List<(string, string[])>();
            foreach (var t in _transfers.Values.ToList())
            {
                if (t.Sender == id)
                {
                    _transfers.Remove(t.Id);
                    if (!t.Finished) cancelled.Add((t.Id, t.Targets.ToArray()));
                }
                else
                {
                    t.Targets.Remove(id);
                }
            }
            _handInTeacher.Remove(id);
            _handInCount.Remove(id);
            return cancelled;
        }
    }
}
