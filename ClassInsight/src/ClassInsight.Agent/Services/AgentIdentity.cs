using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClassInsight.Agent.Services;

/// <summary>
/// This PC's agent key (NFR-SEC-06 agent enrolment): an ECDSA P-256 key created once and kept in
/// %ProgramData%\ClassInsight\agent-key, readable only by SYSTEM and Administrators — a student who
/// could read it could impersonate this PC. The server binds the PC id to this key on first
/// registration. Also reads the optional lab enrolment key from the admin-only registry key
/// HKLM\SOFTWARE\ClassInsight\Agent\Secret (written by the MSI's ENROLLMENTKEY property).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AgentIdentity
{
    private const string SecretKeyPath = @"SOFTWARE\ClassInsight\Agent\Secret";
    private readonly ECDsa _key;
    private readonly byte[] _publicKey;
    private readonly string? _enrollmentKey;

    public AgentIdentity(string? configuredEnrollmentKey, ILogger logger)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassInsight", "agent-key");
        _key = LoadOrCreate(path, logger);
        _publicKey = _key.ExportSubjectPublicKeyInfo();

        _enrollmentKey = ReadSecret(logger);
        if (_enrollmentKey is null && !string.IsNullOrWhiteSpace(configuredEnrollmentKey))
        {
            // Settings files are readable by students; the MSI puts the key in the protected registry instead.
            logger.LogWarning("Using ClassInsight:EnrollmentKey from a settings file, which students can read. " +
                              "Install with the MSI property ENROLLMENTKEY instead.");
            _enrollmentKey = configuredEnrollmentKey.Trim();
        }
    }

    /// <summary>Key id (SHA-256 of the public key), for logs.</summary>
    public string KeyId => AgentEnrollment.KeyId(_publicKey);

    /// <summary>Add the public key, the signature over the server's challenge, and the enrolment proof.</summary>
    public StudentInfo Sign(StudentInfo info, string serverFingerprint, string nonce) => info with
    {
        PublicKey = Convert.ToBase64String(_publicKey),
        Signature = Convert.ToBase64String(AgentEnrollment.Sign(_key, serverFingerprint, nonce, info.MachineId)),
        EnrollmentProof = _enrollmentKey is null ? null : AgentEnrollment.Proof(_enrollmentKey, KeyId, info.MachineId),
    };

    private static ECDsa LoadOrCreate(string path, ILogger logger)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            if (File.Exists(path))
            {
                if (!IsPrivate(new FileInfo(path)))
                    throw new InvalidOperationException(
                        $"{path} can be read or changed by accounts other than SYSTEM and Administrators, so this PC's identity " +
                        "can't be trusted. An administrator should delete it (and this PC's entry in the server's agents.json) to re-enrol.");
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
                return key;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in TrustedSids())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            using (var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData,
                       FileShare.None, 4096, FileOptions.None, security))
                file.Write(key.ExportPkcs8PrivateKey());
            logger.LogInformation("Created this PC's agent key ({Path}).", path);
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not running as the service (console test without rights): a key for this run only.
            logger.LogWarning(ex, "Couldn't keep the agent key in {Path}; using a temporary one (this PC will need re-enrolling).", path);
            return key;
        }
    }

    private static SecurityIdentifier[] TrustedSids()
    {
        var list = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        using var me = WindowsIdentity.GetCurrent();
        if (!me.IsSystem && me.User is { } user) list.Add(user);   // console test runs
        return list.ToArray();
    }

    /// <summary>Owned by a trusted account, and nobody else may read or change it.</summary>
    private static bool IsPrivate(FileInfo file)
    {
        var trusted = TrustedSids();
        var security = file.GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && trusted.Contains(sid)) continue;
            if ((rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData
                 | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.Delete)) != 0) return false;
        }
        return true;
    }

    /// <summary>Owned by SYSTEM/Administrators, and nobody else may read or change it.</summary>
    private static bool IsPrivate(RegistrySecurity security)
    {
        var trusted = new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return false;
        foreach (RegistryAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && trusted.Contains(sid)) continue;
            if ((rule.RegistryRights & (RegistryRights.QueryValues | RegistryRights.SetValue | RegistryRights.CreateSubKey
                 | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.Delete)) != 0) return false;
        }
        return true;
    }

    private static string? ReadSecret(ILogger logger)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(SecretKeyPath);
            if (key?.GetValue("EnrollmentKey") is not string { Length: > 0 } value) return null;

            // The MSI creates this key readable only by SYSTEM and Administrators. If anyone else can
            // read (or change) it, the secret must be treated as exposed: refuse it so IT notices.
            if (!IsPrivate(key.GetAccessControl()))
            {
                logger.LogError("Ignoring the lab enrolment key: HKLM\\{Path} can be read or changed by accounts other than " +
                                "SYSTEM and Administrators, so it may have been exposed. Reinstall with ENROLLMENTKEY (which " +
                                "protects the key) and consider changing the lab key on the server.", SecretKeyPath);
                return null;
            }
            return value.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "No lab enrolment key readable.");
            return null;
        }
    }
}
