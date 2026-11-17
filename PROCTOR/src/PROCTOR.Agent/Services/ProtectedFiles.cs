using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Files the agent trusts (an older server-pin file) must not be writable by students: only SYSTEM,
/// Administrators and — when the agent runs in a console for testing rather than as the service —
/// the account running it. Used to check existing files.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProtectedFiles
{
    private const FileSystemRights Dangerous = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static SecurityIdentifier[] TrustedSids()
    {
        var list = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        using var me = WindowsIdentity.GetCurrent();
        if (!me.IsSystem && me.User is { } user) list.Add(user);
        return list.ToArray();
    }

    /// <summary>Owned by a trusted account and not writable by anyone else.</summary>
    public static bool IsTrusted(FileSystemInfo item)
    {
        var trusted = TrustedSids();
        FileSystemSecurity security = item is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & Dangerous) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || trusted.Contains(sid)) continue;
            // CREATOR OWNER on a folder only applies to what others create inside it, not to the folder or our file.
            if (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (item is DirectoryInfo && sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)) continue;
            return false;
        }
        return true;
    }

    /// <summary>The file and the folder it's in are both trusted.</summary>
    public static bool IsTrustedFile(string path) =>
        File.Exists(path) && IsTrusted(new FileInfo(path)) && IsTrusted(new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!));
}
