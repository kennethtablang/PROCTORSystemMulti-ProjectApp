using System.Text.Json;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Server.Services;

/// <summary>
/// Website limits that outlast a lesson: the class's allow/block list and any PC's own list (by
/// machine id, so they follow the PC across reconnects). Kept in <c>web-limits.json</c> beside the
/// server's key (an admin-only folder), so they survive the last teacher leaving and service
/// restarts, until a teacher changes them.
/// </summary>
public sealed class WebLimitsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<WebLimitsStore> _logger;
    private WebLimits _class = new();
    private readonly Dictionary<string, WebLimits> _byMachine = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public WebLimitsStore(IConfiguration configuration, ILogger<WebLimitsStore> logger)
    {
        _logger = logger;
        var section = configuration.GetSection("PROCTOR");
        var certPath = section["CertificatePath"];
        _path = section["WebLimitsPath"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(string.IsNullOrWhiteSpace(certPath) ? ServerCertificate.DefaultPath : certPath))!, "web-limits.json");
        Load();
    }

    /// <summary>The class's website limits.</summary>
    public WebLimits Class { get { lock (_gate) return _class; } }

    /// <summary>A PC's own website limits, or null when it follows the class.</summary>
    public WebLimits? ForMachine(string? machineId)
    {
        if (string.IsNullOrEmpty(machineId)) return null;
        lock (_gate) return _byMachine.TryGetValue(machineId, out var own) ? own : null;
    }

    public void SetClass(WebLimits limits)
    {
        lock (_gate) _class = ClassroomLimits.CleanWeb(limits);
        Save();
    }

    /// <summary>Give a PC its own website limits; null puts it back on the class's.</summary>
    public void SetMachine(string machineId, WebLimits? limits)
    {
        if (string.IsNullOrEmpty(machineId)) return;
        lock (_gate)
        {
            if (limits is null) _byMachine.Remove(machineId);
            else _byMachine[machineId] = ClassroomLimits.CleanWeb(limits);
        }
        Save();
    }

    private sealed record Stored
    {
        public WebLimits Class { get; init; } = new();
        public Dictionary<string, WebLimits> Machines { get; init; } = new();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            // Anyone who could write this file could open or close websites for the whole lab.
            if (OperatingSystem.IsWindows() && !ServerCertificate.IsTrustedPath(_path))
            {
                _logger.LogWarning("Ignoring {Path}: it is writable by an account other than SYSTEM or Administrators.", _path);
                return;
            }
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Json);
            if (stored is null) return;
            lock (_gate)
            {
                _class = ClassroomLimits.CleanWeb(stored.Class);
                foreach (var (id, limits) in stored.Machines.Take(10_000))
                    if (!string.IsNullOrEmpty(id)) _byMachine[id] = ClassroomLimits.CleanWeb(limits);
            }
            _logger.LogInformation("Website limits restored: class {Mode} ({Sites} sites), {Pcs} PC(s) with their own.",
                _class.WebMode, _class.WebHosts.Count, _byMachine.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read the saved website limits in {Path}; starting without them.", _path);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            // The folder is created (locked down) with the server's key; never create it here with
            // default permissions.
            if (!Directory.Exists(dir)) return;
            Stored stored;
            lock (_gate) stored = new Stored { Class = _class, Machines = new Dictionary<string, WebLimits>(_byMachine) };
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't save the website limits to {Path}; they last until the service restarts.", _path);
        }
    }
}
