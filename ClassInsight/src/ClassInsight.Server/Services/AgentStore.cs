using System.Text.Json;
using ClassInsight.Shared;

namespace ClassInsight.Server.Services;

/// <summary>
/// Which key each lab PC registers with (NFR-SEC-06 agent enrolment). A PC's <c>MachineId</c> is
/// bound to its agent's public key the first time it registers; afterwards only that key can
/// register as that PC, so nobody can take over another PC's tile. New PCs are admitted
/// automatically (<c>AgentEnrollment=Open</c>, the zero-config default) or only with the lab
/// enrolment key (<c>AgentEnrollment=Key</c> + <c>EnrollmentKey</c>).
///
/// Kept in <c>agents.json</c> beside the server certificate, in the folder only SYSTEM,
/// Administrators and the server account can change; a file others could have edited is ignored.
/// To let a re-imaged PC enrol again, delete its entry (or the file) and restart the server.
/// </summary>
public sealed class AgentStore
{
    public enum Outcome { Known, Enrolled, KeyMismatch, EnrollmentKeyRequired, RateLimited, Full }

    private sealed record Entry(string KeyId, string Name, DateTimeOffset Enrolled)
    {
        public DateTimeOffset LastSeen { get; set; } = Enrolled;
    }

    private const int MaxAgents = 20_000;

    /// <summary>PCs not seen for this long are forgotten (they enrol again if they come back).</summary>
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(180);

    // New enrolments are rate-limited per address and server-wide, so a rogue program can't fill the
    // store with made-up PC ids (which would lock real new PCs out). A lab rollout stays well under.
    private readonly int _perAddressPerHour;
    private readonly int _perTenMinutes;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recentByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly TimeProvider _clock;

    private readonly object _gate = new();
    private readonly ILogger<AgentStore> _logger;
    private readonly string _path;
    private readonly Dictionary<string, Entry> _agents;

    public AgentStore(IConfiguration configuration, ILogger<AgentStore> logger) : this(configuration, logger, TimeProvider.System) { }

    public AgentStore(IConfiguration configuration, ILogger<AgentStore> logger, TimeProvider clock)
    {
        _logger = logger;
        _clock = clock;
        var section = configuration.GetSection("ClassInsight");
        _perAddressPerHour = section.GetValue<int?>("EnrollmentsPerAddressPerHour") ?? 5;
        _perTenMinutes = section.GetValue<int?>("EnrollmentsPerTenMinutes") ?? 200;
        var certPath = section["CertificatePath"];
        _path = section["AgentStorePath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(string.IsNullOrWhiteSpace(certPath) ? ServerCertificate.DefaultPath : certPath))!, "agents.json");

        RequireKey = string.Equals(section["AgentEnrollment"], "Key", StringComparison.OrdinalIgnoreCase);
        EnrollmentKey = section["EnrollmentKey"] is { Length: > 0 } k ? k.Trim() : null;
        if (RequireKey && EnrollmentKey is null)
            logger.LogError("AgentEnrollment is \"Key\" but no EnrollmentKey is set: no new PCs can enrol until one is configured.");
        logger.LogInformation(RequireKey
            ? "Agent enrolment: new PCs must present the lab enrolment key."
            : "Agent enrolment: OPEN — a new PC is admitted and bound to its key the first time it registers.");

        _agents = Load();
    }

    /// <summary>New PCs must present the lab enrolment key.</summary>
    public bool RequireKey { get; }

    private string? EnrollmentKey { get; }

    public int Count { get { lock (_gate) return _agents.Count; } }

    /// <summary>Decide whether this (already signature-verified) key may register as this PC.</summary>
    public Outcome Check(string machineId, string keyId, string? proof, string machineName, string remoteAddress)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_agents.TryGetValue(machineId, out var known))
            {
                if (known.KeyId != keyId) return Outcome.KeyMismatch;
                // Keep "last seen" current (for expiry), writing at most once a day per PC.
                var persist = now - known.LastSeen > TimeSpan.FromDays(1);
                known.LastSeen = now;
                if (persist) Save();
                return Outcome.Known;
            }

            if (RequireKey && (EnrollmentKey is null || !AgentEnrollment.ProofMatches(EnrollmentKey, keyId, machineId, proof)))
                return Outcome.EnrollmentKeyRequired;
            if (!AllowNewEnrolment(remoteAddress, now)) return Outcome.RateLimited;
            if (_agents.Count >= MaxAgents)
            {
                PruneExpired(now);
                if (_agents.Count >= MaxAgents) return Outcome.Full;
            }

            _agents[machineId] = new Entry(keyId, machineName, now);
            Save();
            return Outcome.Enrolled;
        }
    }

    private bool AllowNewEnrolment(string address, DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(10)) _recent.Dequeue();
        if (!_recentByAddress.TryGetValue(address, out var mine)) _recentByAddress[address] = mine = new Queue<DateTimeOffset>();
        while (mine.Count > 0 && now - mine.Peek() >= TimeSpan.FromHours(1)) mine.Dequeue();
        if (mine.Count >= _perAddressPerHour || _recent.Count >= _perTenMinutes)
        {
            _logger.LogWarning("Too many new PCs enrolling from {Address} (or overall); refusing for now.", address);
            return false;
        }
        mine.Enqueue(now);
        _recent.Enqueue(now);
        if (_recentByAddress.Count > 10_000) _recentByAddress.Clear();   // bounded bookkeeping
        return true;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var stale = _agents.Where(kv => now - kv.Value.LastSeen > Expiry).Select(kv => kv.Key).ToList();
        foreach (var id in stale) _agents.Remove(id);
        if (stale.Count > 0) _logger.LogInformation("Forgot {Count} PC(s) not seen for {Days} days.", stale.Count, (int)Expiry.TotalDays);
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            if (OperatingSystem.IsWindows() && !ServerCertificate.IsTrustedPath(_path))
            {
                _logger.LogError("Ignoring {Path}: it (or its folder) can be changed by accounts other than SYSTEM, Administrators " +
                                 "or the server's account. Every PC will enrol again.", _path);
                return new(StringComparer.Ordinal);
            }
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
            var agents = loaded is null ? new Dictionary<string, Entry>(StringComparer.Ordinal) : new(loaded, StringComparer.Ordinal);
            foreach (var id in agents.Where(kv => _clock.GetUtcNow() - kv.Value.LastSeen > Expiry).Select(kv => kv.Key).ToList())
                agents.Remove(id);
            _logger.LogInformation("{Count} enrolled PC(s) loaded.", agents.Count);
            return agents;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "Couldn't read {Path}; every PC will enrol again.", _path);
            return new(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            if (OperatingSystem.IsWindows() && Directory.Exists(dir) && !ServerCertificate.IsTrustedDirectory(dir))
            {
                _logger.LogError("Not saving enrolled PCs: {Folder} can be changed by other accounts.", dir);
                return;
            }
            Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_agents));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Couldn't save enrolled PCs to {Path}.", _path);
        }
    }
}
