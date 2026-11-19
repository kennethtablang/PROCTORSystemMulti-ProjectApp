using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace PROCTOR.Agent.Services;

/// <summary>
/// "Block flash drives" (a class limit): USB flash drives and memory cards stop working while it's on.
///  1. Windows' own policy "All removable storage classes: deny all access" (Deny_All), so nothing
///     can be read from or written to them;
///  2. the USB storage driver (USBSTOR) is set not to start, so drives plugged in later don't
///     appear at all;
///  3. drives already plugged in lose their drive letter, so they vanish from File Explorer.
/// Every original value is saved first and put back exactly when the limit is lifted, and a block
/// left behind by a crash is lifted when the agent starts (like "internet off"). Needs the agent
/// service's administrator rights; without them it logs that it couldn't.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UsbStorageBlock
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices";
    private const string UsbStorKey = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
    private const string StateKey = @"SOFTWARE\PROCTOR\Agent\UsbBlock";
    private const int UsbStorDisabled = 4;

    public static bool IsBlockedByUs
    {
        get
        {
            try
            {
                using var state = Registry.LocalMachine.OpenSubKey(StateKey);
                return state?.GetValue("Active") is 1;
            }
            catch { return false; }
        }
    }

    /// <summary>Turn the block on. Returns false if it couldn't (no administrator rights).</summary>
    public static bool Block(ILogger logger)
    {
        try
        {
            using var state = Registry.LocalMachine.CreateSubKey(StateKey, writable: true);
            if (state.GetValue("Active") is not 1)
            {
                // Remember the originals before changing anything.
                using (var usb = Registry.LocalMachine.OpenSubKey(UsbStorKey))
                    state.SetValue("UsbStorStart", usb?.GetValue("Start") is int start ? start : 3, RegistryValueKind.DWord);
                using (var policy = Registry.LocalMachine.OpenSubKey(PolicyKey))
                    state.SetValue("DenyAll", policy?.GetValue("Deny_All") is int deny ? deny : -1, RegistryValueKind.DWord);
                state.SetValue("Active", 1, RegistryValueKind.DWord);
            }

            using (var policy = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true))
                policy.SetValue("Deny_All", 1, RegistryValueKind.DWord);
            using (var usb = Registry.LocalMachine.OpenSubKey(UsbStorKey, writable: true))
                usb?.SetValue("Start", UsbStorDisabled, RegistryValueKind.DWord);

            // Drives already in: take their letters away (remembered, to give back on unblock).
            var removed = (state.GetValue("Letters") as string[] ?? []).ToList();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
            {
                var root = drive.Name;   // "E:\"
                var volume = new StringBuilder(64);
                if (!GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity)) continue;
                if (RunMountvol($"{root.TrimEnd('\\')} /d"))
                {
                    removed.RemoveAll(e => e.StartsWith(root.TrimEnd('\\') + "|", StringComparison.OrdinalIgnoreCase));
                    removed.Add($"{root.TrimEnd('\\')}|{volume}");
                    logger.LogInformation("Flash drive {Drive} removed from view (flash drives blocked).", root);
                }
            }
            state.SetValue("Letters", removed.ToArray(), RegistryValueKind.MultiString);
            logger.LogInformation("Flash drives blocked.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not block flash drives (needs the agent service's administrator rights).");
            return false;
        }
    }

    /// <summary>Lift the block and put every original setting back (no-op if PROCTOR didn't set it).</summary>
    public static void Unblock(ILogger logger, bool quiet)
    {
        try
        {
            using var state = Registry.LocalMachine.OpenSubKey(StateKey, writable: true);
            if (state is null || state.GetValue("Active") is not 1) return;

            using (var usb = Registry.LocalMachine.OpenSubKey(UsbStorKey, writable: true))
                usb?.SetValue("Start", state.GetValue("UsbStorStart") is int start ? start : 3, RegistryValueKind.DWord);
            using (var policy = Registry.LocalMachine.CreateSubKey(PolicyKey, writable: true))
            {
                if (state.GetValue("DenyAll") is int deny && deny >= 0) policy.SetValue("Deny_All", deny, RegistryValueKind.DWord);
                else policy.DeleteValue("Deny_All", throwOnMissingValue: false);
            }

            // Give the letters back to drives that are still plugged in.
            foreach (var entry in state.GetValue("Letters") as string[] ?? [])
            {
                var parts = entry.Split('|', 2);
                if (parts.Length == 2 && parts[1].StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
                    RunMountvol($"{parts[0]} {parts[1]}");
            }

            state.SetValue("Active", 0, RegistryValueKind.DWord);
            state.DeleteValue("Letters", throwOnMissingValue: false);
            if (!quiet) logger.LogInformation("Flash drives allowed again.");
            else logger.LogInformation("Lifted a flash-drive block left over from before the agent restarted.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not lift the flash-drive block.");
        }
    }

    private static bool RunMountvol(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mountvol.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);
}
