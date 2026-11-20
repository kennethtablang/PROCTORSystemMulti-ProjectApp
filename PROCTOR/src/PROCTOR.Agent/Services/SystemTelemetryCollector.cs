using System.Diagnostics;
using System.Net.NetworkInformation;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Samples live machine metrics for the telemetry stream (FR-TEL-01..04): total CPU,
/// physical memory, instantaneous network bandwidth, the top processes by resource use,
/// the foreground window title and (optionally) the foreground browser's host. Each metric is a
/// delta against the previous sample, so the collector is stateful and must be called on a
/// single cadence.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SystemTelemetryCollector
{
    private readonly string _machineId;
    private readonly BrowserActivityProbe? _browser;
    private readonly RateMeter _net = new();
    private readonly int _processorCount = Environment.ProcessorCount;

    // CPU deltas (whole machine).
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _haveCpuBaseline;

    // Per-process CPU deltas.
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _procCpu = new();

    public SystemTelemetryCollector(string machineId, BrowserActivityProbe? browser = null)
    {
        _machineId = machineId;
        _browser = browser;
    }

    public TelemetrySnapshot Collect()
    {
        return new TelemetrySnapshot
        {
            MachineId = _machineId,
            SampledAt = DateTimeOffset.UtcNow,
            System = SampleCpuMemory(),
            Bandwidth = SampleBandwidth(),
            TopProcesses = SampleTopProcesses(),
            ForegroundWindowTitle = SafeForegroundTitle(),
            ActiveUrl = _browser?.CurrentHost,
            RemovableDrives = SampleRemovableDrives(),
            RecentBlockedSites = BlockedSiteLog.Recent(),
            RecentBlockedApps = BlockedAppLog.Recent(),
            IdleSeconds = SafeIdleSeconds(),
            Inventory = SampleInventory(),
        };
    }

    private static int? SafeIdleSeconds()
    {
        try { return NativeMethods.IdleSeconds(); }
        catch { return null; }
    }

    // The PC inventory: in the first sample, then every 30 seconds (disk space and uptime change).
    private static readonly TimeSpan InventoryInterval = TimeSpan.FromSeconds(30);
    private DateTime _inventoryAt = DateTime.MinValue;

    private PcInventory? SampleInventory()
    {
        if (DateTime.UtcNow - _inventoryAt < InventoryInterval) return null;
        _inventoryAt = DateTime.UtcNow;
        try { return PcInventoryReader.Read(); }
        catch { return null; }
    }

    // Flash drives / memory cards: re-read every few seconds (enough to notice one being plugged in).
    private static readonly TimeSpan DriveInterval = TimeSpan.FromSeconds(2);
    private IReadOnlyList<RemovableDrive> _drives = Array.Empty<RemovableDrive>();
    private DateTime _drivesAt = DateTime.MinValue;

    private IReadOnlyList<RemovableDrive> SampleRemovableDrives()
    {
        if (DateTime.UtcNow - _drivesAt < DriveInterval) return _drives;
        _drivesAt = DateTime.UtcNow;
        try
        {
            _drives = System.IO.DriveInfo.GetDrives()
                .Where(d => d.DriveType == System.IO.DriveType.Removable)
                .Select(d =>
                {
                    try
                    {
                        // An empty card reader slot is "removable" too, but not ready: not a drive in use.
                        if (!d.IsReady) return null;
                        return new RemovableDrive
                        {
                            Letter = d.Name.TrimEnd('\\'),
                            Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel.Trim(),
                            SizeBytes = d.TotalSize,
                        };
                    }
                    catch { return null; }   // pulled out while being read
                })
                .OfType<RemovableDrive>()
                .Take(RemovableDrive.MaxReported)
                .ToArray();
        }
        catch
        {
            _drives = Array.Empty<RemovableDrive>();
        }
        return _drives;
    }

    private CpuMemorySample SampleCpuMemory()
    {
        double cpuPercent = 0;
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var i = idle.ToUInt64();
            var k = kernel.ToUInt64();  // kernel time INCLUDES idle on Windows
            var u = user.ToUInt64();

            if (_haveCpuBaseline)
            {
                var idleDelta = i - _prevIdle;
                var totalDelta = (k - _prevKernel) + (u - _prevUser);
                if (totalDelta > 0)
                    cpuPercent = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
            }

            _prevIdle = i; _prevKernel = k; _prevUser = u;
            _haveCpuBaseline = true;
        }

        long usedBytes = 0, totalBytes = 0;
        var mem = new NativeMethods.MEMORYSTATUSEX();
        if (NativeMethods.GlobalMemoryStatusEx(mem))
        {
            totalBytes = (long)mem.ullTotalPhys;
            usedBytes = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
        }

        return new CpuMemorySample
        {
            CpuPercent = Math.Round(cpuPercent, 1),
            MemoryUsedBytes = usedBytes,
            MemoryTotalBytes = totalBytes,
        };
    }

    private BandwidthSample SampleBandwidth()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            catch { /* some adapters refuse stats */ }
        }

        return _net.Sample(rx, tx, DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<ProcessSample> SampleTopProcesses()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new List<(ProcessSample Sample, long Mem)>();
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var pid = p.Id;
                seen.Add(pid);

                double cpuPercent = 0;
                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; }
                catch { cpu = TimeSpan.Zero; }

                if (_procCpu.TryGetValue(pid, out var prev))
                {
                    var wall = (now - prev.At).TotalSeconds;
                    if (wall > 0)
                    {
                        var used = (cpu - prev.Cpu).TotalSeconds;
                        cpuPercent = Math.Clamp(used / (wall * _processorCount) * 100.0, 0, 100);
                    }
                }
                _procCpu[pid] = (cpu, now);

                long mem = 0;
                try { mem = p.WorkingSet64; } catch { }

                samples.Add((new ProcessSample
                {
                    Pid = pid,
                    Name = SafeName(p),
                    CpuPercent = Math.Round(cpuPercent, 1),
                    MemoryBytes = mem,
                }, mem));
            }
            catch { /* process exited mid-enumeration */ }
            finally { p.Dispose(); }
        }

        // Drop CPU history for processes that have since exited.
        foreach (var dead in _procCpu.Keys.Where(k => !seen.Contains(k)).ToArray())
            _procCpu.Remove(dead);

        // Top 15 by CPU then memory, matching the contract's "top N" note.
        return samples
            .OrderByDescending(s => s.Sample.CpuPercent)
            .ThenByDescending(s => s.Mem)
            .Take(15)
            .Select(s => s.Sample)
            .ToArray();
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch { return "?"; }
    }

    private static string? SafeForegroundTitle()
    {
        try { return NativeMethods.GetForegroundWindowTitle(); }
        catch { return null; }
    }
}

/// <summary>
/// Turns raw, ever-increasing NIC byte counters into live bandwidth (FR-TEL-02): bytes/sec as
/// the delta over the elapsed interval — not a raw counter — plus session totals counted from
/// the first sample, i.e. since the agent started (FR-TEL-05). A counter that goes backwards
/// (adapter reset / removed) re-baselines instead of producing a negative or huge spike.
/// </summary>
public sealed class RateMeter
{
    private long _prevRx, _prevTx, _baseRx, _baseTx, _accRx, _accTx;
    private DateTimeOffset _prevAt;
    private bool _have;

    public BandwidthSample Sample(long rx, long tx, DateTimeOffset now)
    {
        double down = 0, up = 0;
        if (_have)
        {
            var secs = (now - _prevAt).TotalSeconds;
            var dRx = rx - _prevRx;
            var dTx = tx - _prevTx;
            if (dRx < 0 || dTx < 0)
            {
                // Counters reset: bank what we had and start a new baseline.
                _accRx += Math.Max(0, _prevRx - _baseRx);
                _accTx += Math.Max(0, _prevTx - _baseTx);
                _baseRx = rx; _baseTx = tx;
            }
            else if (secs > 0)
            {
                down = dRx / secs;
                up = dTx / secs;
            }
        }
        else
        {
            _baseRx = rx; _baseTx = tx;
            _have = true;
        }

        _prevRx = rx; _prevTx = tx; _prevAt = now;

        return new BandwidthSample
        {
            DownloadBytesPerSec = down,
            UploadBytesPerSec = up,
            TotalDownloadBytes = _accRx + (rx - _baseRx),
            TotalUploadBytes = _accTx + (tx - _baseTx),
        };
    }
}
