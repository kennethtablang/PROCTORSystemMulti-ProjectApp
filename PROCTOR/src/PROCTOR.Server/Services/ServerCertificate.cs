using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PROCTOR.Shared;

namespace PROCTOR.Server.Services;

/// <summary>
/// The server's TLS certificate (NFR-SEC-06). IT can supply one (<c>PROCTOR:CertificatePath</c>
/// + <c>CertificatePassword</c>); otherwise a self-signed ECDSA P-256 certificate is generated once
/// and kept, so the server's fingerprint — which agents and teachers pin — survives restarts.
/// The generated file is readable only by SYSTEM, Administrators and the account running the server.
/// </summary>
public sealed class ServerCertificate
{
    private readonly Lazy<X509Certificate2> _certificate;

    public ServerCertificate(IConfiguration configuration, ILogger<ServerCertificate> logger)
    {
        var section = configuration.GetSection("PROCTOR");
        var path = section["CertificatePath"];
        var password = section["CertificatePassword"];
        _certificate = new Lazy<X509Certificate2>(() => Load(path, password, logger));
    }

    public X509Certificate2 Certificate => _certificate.Value;

    /// <summary>What clients pin (SHA-256 of the public key, lower-case hex).</summary>
    public string Fingerprint => ServerTrust.Fingerprint(Certificate);

    public static string DefaultPath => Path.Combine(
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : AppContext.BaseDirectory,
        "PROCTOR", "Server", "server-certificate.pfx");

    private static X509Certificate2 Load(string? configuredPath, string? password, ILogger logger)
    {
        // An IT-supplied PFX (with its password), or the one generated here on an earlier run.
        var path = string.IsNullOrWhiteSpace(configuredPath) ? DefaultPath : configuredPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (File.Exists(path))
        {
            // Whoever can write this file (or its folder) could swap in a key they hold and impersonate
            // the server: only use it if just SYSTEM, Administrators and this account can.
            if (OperatingSystem.IsWindows() && (!IsTrusted(new FileInfo(path)) || !IsTrusted(new DirectoryInfo(dir))))
                throw new InvalidOperationException(
                    $"Refusing the TLS certificate in {path}: it (or its folder) is owned or writable by an account other than " +
                    "SYSTEM, Administrators or the account running the server, so someone else could have planted it. " +
                    "Delete it to have a new one created, or fix its permissions.");
            logger.LogInformation("Using the TLS certificate in {Path}.", path);
            return X509CertificateLoader.LoadPkcs12FromFile(path, string.IsNullOrEmpty(password) ? null : password);
        }

        // First run: make the server's long-lived identity.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=PROCTOR server ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));   // server auth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

        // Folder and file are created already locked down (no window where another account could write).
        var pfx = created.Export(X509ContentType.Pfx);
        if (OperatingSystem.IsWindows())
        {
            var dirInfo = new DirectoryInfo(dir);
            if (dirInfo.Exists)
            {
                if (!IsTrusted(dirInfo))
                    throw new InvalidOperationException($"Refusing to create the server's TLS identity in {dir}: the folder is writable by other accounts.");
            }
            else
            {
                var dirSecurity = new DirectorySecurity();
                dirSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in TrustedSids())
                    dirSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                dirSecurity.CreateDirectory(dir);
            }
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in TrustedSids())
                fileSecurity.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData,
                FileShare.None, 4096, FileOptions.None, fileSecurity);
            file.Write(pfx);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            file.Write(pfx);
        }
        logger.LogInformation("Created the server's TLS identity in {Path}. Keep this file: agents and teachers pin it.", path);

        // Reload from PFX: Windows TLS needs a persisted (not ephemeral) private key.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }

    /// <summary>SYSTEM, Administrators and the account running the server.</summary>
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] TrustedSids() =>
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        WindowsIdentity.GetCurrent().User!,
    ];

    // Rights that would let an account replace or re-permission the file / folder contents.
    private const FileSystemRights Dangerous = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>The file and its folder are both trusted.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsTrustedPath(string path) =>
        IsTrusted(new FileInfo(path)) && IsTrustedDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

    [SupportedOSPlatform("windows")]
    internal static bool IsTrustedDirectory(string dir) => IsTrusted(new DirectoryInfo(dir));

    /// <summary>Owned by a trusted account and not writable by anyone else.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsTrusted(FileSystemInfo item)
    {
        var trusted = TrustedSids();
        FileSystemSecurity security = item is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & Dangerous) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || trusted.Contains(sid)) continue;
            // CREATOR OWNER only applies to things a user creates inside; on the folder itself it grants nothing.
            if (item is DirectoryInfo && sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
            return false;
        }
        return true;
    }
}
