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
/// PCs not seen for 180 days are only forgotten when the store is full.
/// </summary>
public sealed class AgentStore
{
    public enum Outcome { Known, Enrolled, KeyMismatch, EnrollmentKeyRequired, RateLimited, Full }

    private sealed record Entry(string KeyId, string Name, DateTimeOffset Enrolled)
    {
        /// <summary>Null in files written before this was tracked: treated as "seen when loaded", never as old.</summary>
        public DateTimeOffset? LastSeen { get; set; }
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

    // PCs with the lab key skip the per-address limit and the open-mode budget, but still have a
    // server-wide budget of their own, in case the key ever leaks.
    private readonly int _withKeyPerTenMinutes;
    private readonly Queue<DateTimeOffset> _recentWithKey = new();
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
        _withKeyPerTenMinutes = section.GetValue<int?>("KeyEnrollmentsPerTenMinutes") ?? 1000;
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
                var persist = known.LastSeen is not { } seen || now - seen > TimeSpan.FromDays(1);
                known.LastSeen = now;
                if (persist) Save();
                return Outcome.Known;
            }

            var hasLabKey = EnrollmentKey is not null && AgentEnrollment.ProofMatches(EnrollmentKey, keyId, machineId, proof);
            if (RequireKey && !hasLabKey) return Outcome.EnrollmentKeyRequired;
            // PCs installed with the lab key hold the secret: unauthenticated open-mode noise can't use up
            // their budget, but they still have a server-wide budget of their own.
            if (hasLabKey ? !AllowKeyedEnrolment(now) : !AllowNewEnrolment(AddressBucket(remoteAddress), now))
                return Outcome.RateLimited;
            if (_agents.Count >= MaxAgents)
            {
                PruneExpired(now);
                if (_agents.Count >= MaxAgents) return Outcome.Full;
            }

            _agents[machineId] = new Entry(keyId, machineName, now) { LastSeen = now };
            Save();
            return Outcome.Enrolled;
        }
    }

    private bool AllowKeyedEnrolment(DateTimeOffset now)
    {
        while (_recentWithKey.Count > 0 && now - _recentWithKey.Peek() >= TimeSpan.FromMinutes(10)) _recentWithKey.Dequeue();
        if (_recentWithKey.Count >= _withKeyPerTenMinutes)
        {
            _logger.LogWarning("Too many new PCs enrolling with the lab key; refusing for now (has the key leaked?).");
            return false;
        }
        _recentWithKey.Enqueue(now);
        return true;
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
        TrimBuckets(now);
        return true;
    }

    /// <summary>
    /// Rate-limit key for an address: IPv4 as is, IPv6 by its /64 network (one machine can use a whole
    /// /64, so per-address buckets would be trivial to dodge).
    /// </summary>
    public static string AddressBucket(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return address;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new System.Net.IPAddress(bytes) + "/64";
    }

    /// <summary>Keep the bookkeeping bounded without forgetting everyone: drop expired buckets, then the least recent.</summary>
    private void TrimBuckets(DateTimeOffset now)
    {
        if (_recentByAddress.Count <= 10_000) return;
        foreach (var (key, q) in _recentByAddress.ToList())
        {
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromHours(1)) q.Dequeue();
            if (q.Count == 0) _recentByAddress.Remove(key);
        }
        if (_recentByAddress.Count <= 10_000) return;
        foreach (var key in _recentByAddress.OrderBy(kv => kv.Value.Last()).Take(_recentByAddress.Count - 10_000).Select(kv => kv.Key).ToList())
            _recentByAddress.Remove(key);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var stale = _agents.Where(kv => kv.Value.LastSeen is { } seen && now - seen > Expiry).Select(kv => kv.Key).ToList();
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
            // Nothing is forgotten at load: old entries are only pruned when the store is actually full.
            foreach (var entry in agents.Values) entry.LastSeen ??= _clock.GetUtcNow();
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
