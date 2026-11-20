using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PROCTOR.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Applies the room's limits on the student PC (P5), re-checking every ~1.5 s:
///
///  * <b>Web limits</b> (FR-CTRL-06): when the browser in front is on a site the policy doesn't
///    allow, that tab is closed (Ctrl+W to that browser window only) and a notice tells the student
///    why. This is "soft" enforcement from the address bar — it needs no proxy or browser
///    extension, and new-tab / start pages (no host) are always allowed.
///  * <b>Blocked apps</b>: listed programs running in the student's session are closed. Windows
///    itself, the shell and PROCTOR are never touched (<see cref="ClassroomLimits.ProtectedApps"/>).
///  * <b>Internet off</b>: Windows Firewall outbound block rules for every public address range;
///    private LAN ranges (so the lab, the classroom server and screen streaming) stay reachable.
///    Needs the agent's service (SYSTEM) rights; in dev mode it is logged as unavailable.
///  * <b>Mute</b>: the default playback device is muted and kept muted; the student's previous
///    mute state is restored when the limit is lifted.
///
/// Everything is lifted by <see cref="Apply"/> with an empty policy — which the server sends when
/// the last teacher leaves, and the agent applies itself when it loses the server.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PolicyEnforcer : IDisposable
{
    private const string FirewallRuleName = "PROCTOR - internet blocked by teacher";

    // Public IPv4 space minus private (10/8, 172.16/12, 192.168/16), CGNAT (100.64/10), loopback,
    // link-local and multicast; plus IPv6 global unicast.
    private const string PublicRanges =
        "1.0.0.0-9.255.255.255,11.0.0.0-100.63.255.255,100.128.0.0-126.255.255.255," +
        "128.0.0.0-169.253.255.255,169.255.0.0-172.15.255.255,172.32.0.0-192.167.255.255," +
        "192.169.0.0-223.255.255.255,2000::-3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";

    private readonly ILogger _logger;
    private readonly BrowserActivityProbe? _browser;
    private readonly ClassroomUi? _ui;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private ClassPolicy _policy = new();
    private bool _internetBlocked;
    private bool? _mutedBefore;
    private readonly Dictionary<string, DateTime> _lastNotice = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastTabClose;

    public PolicyEnforcer(ILogger logger, BrowserActivityProbe? browser, ClassroomUi? ui)
    {
        _logger = logger;
        _browser = browser;
        _ui = ui;
        // A previous agent that crashed while the internet was off must not leave it off — and the
        // same for flash drives.
        RemoveFirewallRule(quiet: true);
        UsbStorageBlock.Unblock(_logger, quiet: true);
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5));
    }

    public ClassPolicy Current { get { lock (_gate) return _policy; } }

    /// <summary>Switch to a new policy (an empty one lifts every limit).</summary>
    public void Apply(ClassPolicy policy)
    {
        policy = ClassroomLimits.Clean(policy);
        ClassPolicy was;
        lock (_gate)
        {
            was = _policy;
            _policy = policy;

            if (policy.BlockInternet != _internetBlocked)
            {
                if (policy.BlockInternet) AddFirewallRule();
                else RemoveFirewallRule(quiet: false);
            }

            if (policy.MuteSound && !was.MuteSound)
            {
                _mutedBefore = AudioMute.TryGet();
                AudioMute.TrySet(true, _logger);
            }
            else if (!policy.MuteSound && was.MuteSound && _mutedBefore is { } before)
            {
                AudioMute.TrySet(before, _logger);
                _mutedBefore = null;
            }

            if (policy.BlockUsbStorage && !was.BlockUsbStorage)
            {
                if (UsbStorageBlock.Block(_logger)) _ui?.ShowNotice("Your teacher has blocked flash drives.");
            }
            else if (!policy.BlockUsbStorage && was.BlockUsbStorage)
            {
                UsbStorageBlock.Unblock(_logger, quiet: false);
                _ui?.ShowNotice("Flash drives work again. Plug yours in again if it doesn't appear.");
            }
        }

        if (policy.ExamMode != was.ExamMode || policy.ExamTitle != was.ExamTitle)
            _ui?.ShowExam(policy.ExamMode, policy.ExamTitle);

        if (!policy.IsEmpty)
            _logger.LogInformation("Limits applied: web {Mode} ({Hosts}), {Apps} blocked app(s), only-these-programs {Only} ({Allowed}), internet {Net}, mute {Mute}.",
                policy.WebMode, policy.WebHosts.Count, policy.BlockedApps.Count, policy.AllowOnlyApps, policy.AllowedApps.Count, policy.BlockInternet ? "off" : "on", policy.MuteSound);
        else if (!was.IsEmpty)
            _logger.LogInformation("Limits lifted.");
    }

    private void Tick()
    {
        ClassPolicy policy;
        lock (_gate) policy = _policy;
        if (policy.IsEmpty) return;

        try { EnforceWeb(policy); } catch (Exception ex) { _logger.LogDebug(ex, "Web limit check failed."); }
        try { EnforceApps(policy); } catch (Exception ex) { _logger.LogDebug(ex, "App limit check failed."); }
        if (policy.MuteSound) AudioMute.TrySet(true, _logger);   // keep it muted
        // A memory card (its reader doesn't use the USB storage driver) or a drive that slipped
        // through: take its letter away too. Checked every few seconds.
        if (policy.BlockUsbStorage && ++_usbCheck % 4 == 0)
        {
            try
            {
                if (DriveInfo.GetDrives().Any(d => d.DriveType == DriveType.Removable && d.IsReady))
                    UsbStorageBlock.Block(_logger);
            }
            catch { }
        }
    }

    private int _usbCheck;

    private void EnforceWeb(ClassPolicy policy)
    {
        if (policy.WebMode == WebLimitMode.Off || _browser is null) return;
        var (host, window) = _browser.Current;
        if (host is null || window == IntPtr.Zero || ClassroomLimits.IsSiteAllowed(host, policy)) return;

        // Close only that tab, only if that browser window is still the one in front, and not
        // more than about once a second (the probe needs a moment to see the next page).
        if (DateTime.UtcNow - _lastTabClose < TimeSpan.FromSeconds(1.2)) return;
        if (NativeMethods.GetForegroundWindow() != window) return;

        SendCtrlW();
        _lastTabClose = DateTime.UtcNow;
        _browser.Invalidate();
        BlockedSiteLog.Record(host);   // for the teacher's activity log
        Notice(host, policy.WebMode == WebLimitMode.AllowOnlyListed
            ? $"{host} isn't on your teacher's list of allowed sites."
            : $"{host} is blocked by your teacher.");
        _logger.LogInformation("Closed a browser tab on {Host} (web limits).", host);
    }

    private void EnforceApps(ClassPolicy policy)
    {
        if (policy.BlockedApps.Count == 0 && !policy.AllowOnlyApps) return;
        var blocked = new HashSet<string>(policy.BlockedApps, StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(policy.AllowedApps, StringComparer.OrdinalIgnoreCase);
        // "Only these programs": only programs with a window count (background helpers and services
        // are left alone), found with one pass over the desktop's windows.
        var withWindows = policy.AllowOnlyApps ? NativeMethods.ProcessesWithWindows() : null;
        NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var mySession);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var name = p.ProcessName;
                    if (p.Id == Environment.ProcessId) continue;
                    var isBlocked = blocked.Contains(name) && !ClassroomLimits.ProtectedApps.Contains(name);
                    // Exact names only, and a Windows / PROCTOR name only counts when the program really
                    // runs from Windows or PROCTOR's folder: a game renamed to explorer.exe is still closed.
                    var notAllowed = withWindows is not null && withWindows.Contains(p.Id)
                        && !(allowed.Contains(name) && IsGenuinelyNamed(p, name))
                        && !(ClassroomLimits.AlwaysAllowedApps.Contains(name) && IsGenuineSystemProgram(p));
                    if (!isBlocked && !notAllowed) continue;
                    if (p.SessionId != mySession) continue;   // only the student's own session
                    p.Kill();
                    BlockedAppLog.Record(name);   // for the teacher's activity log and alerts
                    Notice(name, isBlocked ? $"{name} is blocked by your teacher." : $"{name} isn't allowed right now. Your teacher chose which programs you can use.");
                    _logger.LogInformation("Closed blocked app {Name} (pid {Pid}).", name, p.Id);
                }
                catch { /* exited, or access denied (elevated process in dev mode) */ }
            }
        }
    }

    private static readonly string[] SystemFolders = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView"),
    }
    .Where(d => !string.IsNullOrEmpty(d))
    .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)) + Path.DirectorySeparatorChar)
    .ToArray();

    /// <summary>
    /// True when the program's file is inside Windows, PROCTOR's own folder or the WebView2 runtime.
    /// Fails closed: a program whose path can't be read doesn't count as Windows' own.
    /// </summary>
    private static bool IsGenuineSystemProgram(Process p) =>
        FullPath(p) is { } path && SystemFolders.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase));

    private static string? FullPath(Process p)
    {
        try { return NativeMethods.ProcessImagePath(p.Id) is { Length: > 0 } path ? Path.GetFullPath(path) : null; }
        catch { return null; }
    }

    // Folders a standard user (the student) can't write to: a program there wasn't planted by them.
    private static readonly string[] TrustedFolders = SystemFolders.Concat(new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    }
    .Where(d => !string.IsNullOrEmpty(d))
    .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)) + Path.DirectorySeparatorChar))
    .ToArray();

    // Checks can overlap (a slow pass and the next timer tick), so the cache is concurrent. Keyed by
    // path + size + last write, so a file swapped for another at the same path is checked again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _genuineByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A program the teacher allowed by name really is that program, not something renamed to look
    /// like it: it runs from a folder students can't write to, or its built-in original file name
    /// (version info, e.g. "chrome.exe" or a .NET app's "Program1.dll") matches its name.
    /// </summary>
    private bool IsGenuinelyNamed(Process p, string name)
    {
        // Fails closed: anything a student starts can be read by the agent (it runs as SYSTEM).
        if (FullPath(p) is not { } full) return false;
        // Trusted folders can't be written by the student: no need to look further (or cache).
        if (TrustedFolders.Any(dir => full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))) return true;

        string key;
        try
        {
            var file = new FileInfo(full);
            key = $"{full}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        catch { return false; }
        if (_genuineByPath.TryGetValue(key, out var known)) return known;

        bool ok;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(full);
            var names = new[] { info.OriginalFilename, info.InternalName }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Path.GetFileNameWithoutExtension(n!.Trim()))
                .ToArray();
            // Fail closed: outside the trusted folders a program needs a matching built-in name;
            // one with no version info at all could be anything renamed.
            ok = names.Length > 0 && names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            ok = false;
        }
        if (_genuineByPath.Count > 500) _genuineByPath.Clear();
        _genuineByPath[key] = ok;
        if (!ok) _logger.LogInformation("{Path} is named like the allowed program {Name} but isn't it; closing it.", full, name);
        return ok;
    }

    private void Notice(string key, string text)
    {
        if (_lastNotice.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(8)) return;
        _lastNotice[key] = DateTime.UtcNow;
        _ui?.ShowNotice(text);
    }

    // ---- internet off (Windows Firewall) ----------------------------------

    private void AddFirewallRule()
    {
        RemoveFirewallRule(quiet: true);
        var ok = Netsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=out action=block remoteip={PublicRanges} enable=yes");
        _internetBlocked = ok;
        if (ok) { _logger.LogInformation("Internet access blocked (LAN still reachable)."); _ui?.ShowNotice("Your teacher has turned off the internet."); }
        else _logger.LogWarning("Could not block internet access (needs the agent service's admin rights).");
    }

    private void RemoveFirewallRule(bool quiet)
    {
        var ok = Netsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        if (_internetBlocked && !quiet)
        {
            if (ok) _ui?.ShowNotice("Internet access is back on.");
            _logger.LogInformation("Internet access restored.");
        }
        _internetBlocked = false;
    }

    private static bool Netsh(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args)
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

    // ---- Ctrl+W to the front browser --------------------------------------

    private static void SendCtrlW()
    {
        const byte VK_CONTROL = 0x11, VK_W = 0x57;
        const uint KEYEVENTF_KEYUP = 0x0002;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_W, 0, 0, UIntPtr.Zero);
        keybd_event(VK_W, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public void Dispose()
    {
        _timer.Dispose();
        Apply(new ClassPolicy());   // lift everything on the way out
    }
}

/// <summary>Mute/unmute the default playback device via Core Audio (IAudioEndpointVolume).</summary>
[SupportedOSPlatform("windows")]
internal static class AudioMute
{
    public static bool? TryGet()
    {
        try
        {
            var vol = Endpoint();
            if (vol is null) return null;
            Marshal.ThrowExceptionForHR(vol.GetMute(out var muted));
            return muted;
        }
        catch { return null; }
    }

    public static void TrySet(bool mute, ILogger logger)
    {
        try
        {
            var vol = Endpoint();
            if (vol is null) return;
            vol.GetMute(out var current);
            if (current == mute) return;
            var ctx = Guid.Empty;
            Marshal.ThrowExceptionForHR(vol.SetMute(mute, ref ctx));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not change the mute state.");
        }
    }

    private static IAudioEndpointVolume? Endpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0 || device is null) return null;
        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj) != 0) return null;
        return obj as IAudioEndpointVolume;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
