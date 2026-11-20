using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Reads what this PC is for the teacher's lab inventory: maker and model, processor, memory,
/// space on the Windows drive, the Windows edition and build, when it last started, and the agent
/// version. Everything comes from the registry and plain Win32 calls (no WMI), so it is quick
/// enough to read every 30 seconds.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PcInventoryReader
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string WindowsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // Hardware and the Windows edition don't change while the agent runs; read them once.
    private static readonly Lazy<(string? Maker, string? Model, string? Cpu, string? Windows)> Fixed = new(ReadFixed);

    public static PcInventory Read()
    {
        var (maker, model, cpu, windows) = Fixed.Value;

        long memory = 0;
        var mem = new NativeMethods.MEMORYSTATUSEX();
        if (NativeMethods.GlobalMemoryStatusEx(mem)) memory = (long)mem.ullTotalPhys;

        long diskTotal = 0, diskFree = 0;
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                diskTotal = drive.TotalSize;
                diskFree = drive.AvailableFreeSpace;
            }
        }
        catch { /* unreadable: left at 0 */ }

        return new PcInventory
        {
            Manufacturer = maker,
            Model = model,
            CpuName = cpu,
            CpuCores = Environment.ProcessorCount,
            MemoryTotalBytes = memory,
            DiskTotalBytes = diskTotal,
            DiskFreeBytes = diskFree,
            Windows = windows,
            BootedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
            AgentVersion = ProductVersion.Text,
        };
    }

    private static (string?, string?, string?, string?) ReadFixed()
    {
        var maker = Value(BiosKey, "SystemManufacturer");
        var model = Value(BiosKey, "SystemProductName");
        // Placeholder strings some boards ship with mean "unknown".
        if (IsPlaceholder(maker)) maker = null;
        if (IsPlaceholder(model)) model = null;
        var cpu = Value(CpuKey, "ProcessorNameString");
        return (maker, model, cpu is null ? null : string.Join(' ', cpu.Split(' ', StringSplitOptions.RemoveEmptyEntries)), ReadWindows());
    }

    /// <summary>"Windows 11 Pro 24H2 (build 26100.2605)".</summary>
    private static string? ReadWindows()
    {
        var product = Value(WindowsKey, "ProductName");
        var release = Value(WindowsKey, "DisplayVersion") ?? Value(WindowsKey, "ReleaseId");
        var build = Value(WindowsKey, "CurrentBuildNumber") ?? Value(WindowsKey, "CurrentBuild");
        int? ubr = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WindowsKey);
            if (key?.GetValue("UBR") is int u) ubr = u;
        }
        catch { }

        if (product is null) return build is null ? null : $"Windows (build {build})";
        // Windows 11 still says "Windows 10" in ProductName; the build number tells them apart.
        if (int.TryParse(build, out var b) && b >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            product = "Windows 11" + product["Windows 10".Length..];
        var text = product;
        if (release is not null) text += " " + release;
        if (build is not null) text += $" (build {build}{(ubr is { } n ? "." + n : "")})";
        return text;
    }

    private static bool IsPlaceholder(string? value) =>
        value is null
        || value.Contains("To be filled", StringComparison.OrdinalIgnoreCase)
        || value.Contains("System Product Name", StringComparison.OrdinalIgnoreCase)
        || value.Contains("System manufacturer", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Default string", StringComparison.OrdinalIgnoreCase)
        || value.Equals("O.E.M.", StringComparison.OrdinalIgnoreCase);

    private static string? Value(string keyPath, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(name) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
