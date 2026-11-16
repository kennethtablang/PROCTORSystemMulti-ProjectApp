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
    public enum Outcome { Known, Enrolled, KeyMismatch, EnrollmentKeyRequired, Full }

    private sealed record Entry(string KeyId, string Name, DateTimeOffset Enrolled);

    private const int MaxAgents = 20_000;
    private readonly object _gate = new();
    private readonly ILogger<AgentStore> _logger;
    private readonly string _path;
    private readonly Dictionary<string, Entry> _agents;

    public AgentStore(IConfiguration configuration, ILogger<AgentStore> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("ClassInsight");
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
    public Outcome Check(string machineId, string keyId, string? proof, string machineName)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(machineId, out var known))
                return known.KeyId == keyId ? Outcome.Known : Outcome.KeyMismatch;

            if (RequireKey && (EnrollmentKey is null || !AgentEnrollment.ProofMatches(EnrollmentKey, keyId, machineId, proof)))
                return Outcome.EnrollmentKeyRequired;
            if (_agents.Count >= MaxAgents) return Outcome.Full;

            _agents[machineId] = new Entry(keyId, machineName, DateTimeOffset.UtcNow);
            Save();
            return Outcome.Enrolled;
        }
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
            _logger.LogInformation("{Count} enrolled PC(s) loaded.", loaded?.Count ?? 0);
            return loaded is null ? new(StringComparer.Ordinal) : new(loaded, StringComparer.Ordinal);
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
