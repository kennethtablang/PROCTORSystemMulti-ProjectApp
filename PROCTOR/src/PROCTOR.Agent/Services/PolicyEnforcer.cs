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
        // A previous agent that crashed while the internet was off must not leave it off.
        RemoveFirewallRule(quiet: true);
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
        }

        if (!policy.IsEmpty)
            _logger.LogInformation("Limits applied: web {Mode} ({Hosts}), {Apps} blocked app(s), internet {Net}, mute {Mute}.",
                policy.WebMode, policy.WebHosts.Count, policy.BlockedApps.Count, policy.BlockInternet ? "off" : "on", policy.MuteSound);
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
    }

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
        Notice(host, policy.WebMode == WebLimitMode.AllowOnlyListed
            ? $"{host} isn't on your teacher's list of allowed sites."
            : $"{host} is blocked by your teacher.");
        _logger.LogInformation("Closed a browser tab on {Host} (web limits).", host);
    }

    private void EnforceApps(ClassPolicy policy)
    {
        if (policy.BlockedApps.Count == 0) return;
        var blocked = new HashSet<string>(policy.BlockedApps, StringComparer.OrdinalIgnoreCase);
        NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var mySession);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var name = p.ProcessName;
                    if (!blocked.Contains(name) || ClassroomLimits.ProtectedApps.Contains(name)) continue;
                    if (p.SessionId != mySession) continue;   // only the student's own session
                    p.Kill();
                    Notice(name, $"{name} is blocked by your teacher.");
                    _logger.LogInformation("Closed blocked app {Name} (pid {Pid}).", name, p.Id);
                }
                catch { /* exited, or access denied (elevated process in dev mode) */ }
            }
        }
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
