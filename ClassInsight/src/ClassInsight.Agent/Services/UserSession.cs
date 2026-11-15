using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using static ClassInsight.Agent.Services.NativeMethods;

namespace ClassInsight.Agent.Services;

/// <summary>
/// Actions taken on behalf of the teacher that must happen <i>as the signed-in student</i>, or on
/// the student's session, rather than as the agent's own SYSTEM account (P5):
///
///  * <see cref="OpenUrl"/> — under the service the agent runs as SYSTEM; a browser started as
///    SYSTEM would be a privilege-escalation hole, so the page is opened with the student's own
///    token (<c>WTSQueryUserToken</c>). In console/dev mode it's simply the current user.
///  * <see cref="LogOff"/>, <see cref="Restart"/>, <see cref="ShutDown"/> — power actions, with a
///    countdown so work can be saved.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserSession
{
    private static bool RunningAsSystem
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.IsSystem;
        }
    }

    private static uint CurrentSessionId =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out var s) ? s : WTSGetActiveConsoleSessionId();

    /// <summary>Open an (already validated) http/https URL in the student's default browser.</summary>
    public static void OpenUrl(string url, ILogger logger)
    {
        if (!Shared.Contracts.ClassroomLimits.TryCleanUrl(url, out var clean)) return;   // defence in depth

        if (!RunningAsSystem)
        {
            Process.Start(new ProcessStartInfo(clean) { UseShellExecute = true })?.Dispose();
            return;
        }

        // explorer.exe <url> hands the URL to the user's default browser, in the user's context.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        StartAsUser(explorer, $"\"{explorer}\" \"{clean}\"", logger);
    }

    public static void LogOff(ILogger logger)
    {
        if (!WTSLogoffSession(WTS_CURRENT_SERVER_HANDLE, CurrentSessionId, false))
            logger.LogWarning("Log off failed ({Error}).", Marshal.GetLastWin32Error());
    }

    public static void Restart(ILogger logger) => Shutdown("/r", logger);

    public static void ShutDown(ILogger logger) => Shutdown("/s", logger);

    private static void Shutdown(string mode, ILogger logger)
    {
        var seconds = Shared.Contracts.ClassroomLimits.PowerCountdownSeconds;
        var what = mode == "/r" ? "restart" : "shut down";
        var psi = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            $"{mode} /t {seconds} /c \"Your teacher is going to {what} this computer in {seconds} seconds. Save your work.\" /d p:4:1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            if (p is { ExitCode: not 0 }) logger.LogWarning("shutdown.exe {Mode} exited with {Code}.", mode, p.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not {What} the computer.", what);
        }
    }

    /// <summary>Start a process with the signed-in user's token in their session (agent running as SYSTEM).</summary>
    private static void StartAsUser(string exe, string commandLine, ILogger logger)
    {
        IntPtr userToken = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
        PROCESS_INFORMATION pi = default;
        try
        {
            if (!WTSQueryUserToken(CurrentSessionId, out userToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken (no user signed in?)");
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
            CreateEnvironmentBlock(out env, primary, false);

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (!CreateProcessAsUser(primary, exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT, env, null, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the page for the signed-in student.");
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
