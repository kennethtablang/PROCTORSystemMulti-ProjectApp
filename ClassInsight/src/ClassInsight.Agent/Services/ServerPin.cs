using System.IO;
using System.Runtime.Versioning;
using ClassInsight.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClassInsight.Agent.Services;

/// <summary>
/// Which classroom server this agent trusts (NFR-SEC-06). IT can pin it explicitly with
/// <c>ClassInsight:ServerFingerprint</c> (strict: the agent never talks to any other server, and
/// remote control is only possible this way). Without it the agent trusts the first server it
/// connects to and remembers that key, refusing any server presenting a different key afterwards.
///
/// The remembered key lives in <c>HKLM\SOFTWARE\ClassInsight\Agent</c> (value
/// <c>TrustedServerFingerprint</c>), which only Administrators and SYSTEM can write — so a student
/// can neither change it nor delete it to make the agent trust a new server. An older pin file in
/// %ProgramData% is imported once, and only if students couldn't have written it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ServerPin
{
    private const string KeyPath = @"SOFTWARE\ClassInsight\Agent";
    private const string ValueName = "TrustedServerFingerprint";
    private const string EndpointValueName = "LastServerEndpoint";
    private string? _lastEndpoint;
    private readonly ILogger _logger;
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

        _remembered = ReadRegistry() ?? ImportLegacyFile();
        _lastEndpoint = ReadValue(EndpointValueName);
    }

    /// <summary>
    /// host:port where the trusted server was last reached (HKLM, so it survives restarts): probed first,
    /// so discovery noise can't hide it.
    /// </summary>
    public string? LastServerEndpoint
    {
        get => _lastEndpoint;
        set
        {
            if (value is null || value == _lastEndpoint) return;
            _lastEndpoint = value;
            WriteValue(EndpointValueName, value);
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
        if (!WriteRegistry(fingerprint))
            _logger.LogWarning("Couldn't store the trusted server in HKLM (the agent isn't running as the service); " +
                               "it is kept until the agent restarts.");
        _logger.LogWarning("First connection: now trusting the classroom server with fingerprint {Fingerprint}. " +
                           "Set ClassInsight:ServerFingerprint to pin it explicitly.", ServerTrust.Format(fingerprint));
    }

    private string? ReadRegistry() => ServerTrust.Normalize(ReadValue(ValueName));

    private bool WriteRegistry(string fingerprint) => WriteValue(ValueName, fingerprint);

    private string? ReadValue(string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(KeyPath);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "Couldn't read {Name} from the registry.", name);
            return null;
        }
    }

    private bool WriteValue(string name, string value)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(KeyPath, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogDebug(ex, "Couldn't write {Name} to the registry.", name);
            return false;
        }
    }

    /// <summary>Earlier builds kept the pin in %ProgramData%\ClassInsight\server-pin: import it once if it's trustworthy.</summary>
    private string? ImportLegacyFile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassInsight", "server-pin");
        try
        {
            if (!File.Exists(path)) return null;
            if (!ProtectedFiles.IsTrustedFile(path))
            {
                _logger.LogWarning("Ignoring {Path}: students could have changed it.", path);
                return null;
            }
            var pin = ServerTrust.Normalize(File.ReadAllText(path));
            if (pin is not null && WriteRegistry(pin)) File.Delete(path);
            return pin;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't read the old server pin file.");
            return null;
        }
    }
}
