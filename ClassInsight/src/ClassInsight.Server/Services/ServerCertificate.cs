using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using ClassInsight.Shared;

namespace ClassInsight.Server.Services;

/// <summary>
/// The server's TLS certificate (NFR-SEC-06). IT can supply one (<c>ClassInsight:CertificatePath</c>
/// + <c>CertificatePassword</c>); otherwise a self-signed ECDSA P-256 certificate is generated once
/// and kept, so the server's fingerprint — which agents and teachers pin — survives restarts.
/// The generated file is readable only by SYSTEM, Administrators and the account running the server.
/// </summary>
public sealed class ServerCertificate
{
    private readonly Lazy<X509Certificate2> _certificate;

    public ServerCertificate(IConfiguration configuration, ILogger<ServerCertificate> logger)
    {
        var section = configuration.GetSection("ClassInsight");
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
        "ClassInsight", "Server", "server-certificate.pfx");

    private static X509Certificate2 Load(string? configuredPath, string? password, ILogger logger)
    {
        // An IT-supplied PFX (with its password), or the one generated here on an earlier run.
        var path = string.IsNullOrWhiteSpace(configuredPath) ? DefaultPath : configuredPath;
        if (File.Exists(path))
        {
            logger.LogInformation("Using the TLS certificate in {Path}.", path);
            return X509CertificateLoader.LoadPkcs12FromFile(path, string.IsNullOrEmpty(password) ? null : password);
        }

        // First run: make the server's long-lived identity.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=ClassInsight server ({Environment.MachineName})", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));   // server auth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pfx = created.Export(X509ContentType.Pfx);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.Write(pfx);
        if (OperatingSystem.IsWindows()) Protect(path, logger);
        logger.LogInformation("Created the server's TLS identity in {Path}. Keep this file: agents and teachers pin it.", path);

        // Reload from PFX: Windows TLS needs a persisted (not ephemeral) private key.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }

    [SupportedOSPlatform("windows")]
    private static void Protect(string path, ILogger logger)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                WindowsIdentity.GetCurrent().User!,
            })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't restrict access to {Path}; protect it manually (it holds the server's private key).", path);
        }
    }
}
