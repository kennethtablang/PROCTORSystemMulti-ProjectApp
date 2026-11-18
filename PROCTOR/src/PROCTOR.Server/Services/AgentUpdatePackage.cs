using System.Runtime.Versioning;
using System.Security.Cryptography;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Server.Services;

/// <summary>
/// The student-agent MSI the teacher setup installs next to the server
/// (<c>agent-update\PROCTOR.Agent.msi</c>). Agents older than it fetch and install it, so the
/// whole lab follows the teacher's PC after a teacher update.
///
/// An agent runs this MSI as SYSTEM, so it's offered only when nobody but SYSTEM / Administrators
/// can have written it: a server run from a developer's build folder (writable by that user)
/// offers nothing, and neither does a file whose permissions were loosened.
/// </summary>
public sealed class AgentUpdatePackage
{
    public const long MaxSize = 400L * 1024 * 1024;

    private readonly ILogger<AgentUpdatePackage> _logger;
    private readonly Lazy<(AgentUpdateInfo? Info, string? Path)> _package;

    public AgentUpdatePackage(ILogger<AgentUpdatePackage> logger, IConfiguration config)
    {
        _logger = logger;
        var dir = config["PROCTOR:AgentUpdateDir"] is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "agent-update");
        _package = new(() => Load(dir));
    }

    /// <summary>What to tell agents, or null when there is nothing (trusted) to offer.</summary>
    public AgentUpdateInfo? Info => _package.Value.Info;

    /// <summary>The MSI's path, when <see cref="Info"/> is set.</summary>
    public string? FilePath => _package.Value.Path;

    private (AgentUpdateInfo?, string?) Load(string dir)
    {
        var msi = Path.Combine(dir, "PROCTOR.Agent.msi");
        var versionFile = Path.Combine(dir, "version.txt");
        if (!File.Exists(msi) || !File.Exists(versionFile)) return (null, null);

        try
        {
            if (OperatingSystem.IsWindows() && !IsProtected(msi, versionFile, dir))
            {
                _logger.LogWarning("Not offering the agent update in {Dir}: it is writable by accounts other than SYSTEM and " +
                                   "Administrators (install the teacher setup instead of running from a build folder).", dir);
                return (null, null);
            }

            var version = ProductVersion.Parse(File.ReadAllText(versionFile));
            var size = new FileInfo(msi).Length;
            if (version is null || size <= 0 || size > MaxSize) return (null, null);

            string hash;
            using (var stream = File.OpenRead(msi))
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            var info = new AgentUpdateInfo { Version = ProductVersion.Format(version), Sha256 = hash, Size = size };
            _logger.LogInformation("Offering student agent {Version} to older agents.", info.Version);
            return (info, msi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read the agent update in {Dir}.", dir);
            return (null, null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProtected(string msi, string versionFile, string dir) =>
        ServerCertificate.IsTrusted(new FileInfo(msi), adminOnly: true)
        && ServerCertificate.IsTrusted(new FileInfo(versionFile), adminOnly: true)
        && ServerCertificate.IsTrustedDirectory(dir, adminOnly: true);
}
