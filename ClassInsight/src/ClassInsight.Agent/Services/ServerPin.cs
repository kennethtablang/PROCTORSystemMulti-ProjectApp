using System.IO;
using ClassInsight.Shared;
using Microsoft.Extensions.Logging;

namespace ClassInsight.Agent.Services;

/// <summary>
/// Which classroom server this agent trusts (NFR-SEC-06). IT can pin it explicitly with
/// <c>ClassInsight:ServerFingerprint</c> (strict: the agent never talks to any other server, and
/// remote control is only possible this way). Without it the agent trusts the first server it
/// connects to and remembers that key in %ProgramData%\ClassInsight\server-pin, refusing any server
/// presenting a different key afterwards.
/// </summary>
internal sealed class ServerPin
{
    private const string FileName = "server-pin";
    private readonly ILogger _logger;
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassInsight", FileName);
    private string? _remembered;

    public ServerPin(string? configured, ILogger logger)
    {
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Configured = ServerTrust.Normalize(configured);
            if (Configured is null)
                logger.LogError("ClassInsight:ServerFingerprint isn't a valid fingerprint (64 hex digits, as printed by the server); ignoring it.");
        }
        try
        {
            if (File.Exists(_path)) _remembered = ServerTrust.Normalize(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Couldn't read the remembered server pin.");
        }
    }

    /// <summary>The fingerprint IT configured, if any (strict pinning).</summary>
    public string? Configured { get; }

    /// <summary>The key to require: configured, else the one remembered from the first connection.</summary>
    public string? Current => Configured ?? _remembered;

    public bool IsStrict => Configured is not null;

    /// <summary>First connection with no configured pin: trust this key from now on.</summary>
    public void Remember(string fingerprint)
    {
        if (Configured is not null || _remembered is not null) return;
        _remembered = fingerprint;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save the server pin; it is kept until the agent restarts.");
        }
        _logger.LogWarning("First connection: now trusting the classroom server with fingerprint {Fingerprint}. " +
                           "Set ClassInsight:ServerFingerprint to pin it explicitly.", ServerTrust.Format(fingerprint));
    }
}
