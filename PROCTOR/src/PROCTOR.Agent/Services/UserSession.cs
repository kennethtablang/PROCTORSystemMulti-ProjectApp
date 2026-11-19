using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using static PROCTOR.Agent.Services.NativeMethods;

namespace PROCTOR.Agent.Services;

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

    // ---- file access as the student -----------------------------------------

    /// <summary>Known folders used for file transfer.</summary>
    public static readonly Guid DesktopFolder = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid DocumentsFolder = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");

    /// <summary>
    /// Run file work <b>as the signed-in student</b> (impersonating their token when the agent is
    /// SYSTEM). The student's own permissions then apply: a junction or link planted in their folder
    /// can't redirect the agent into system or other users' files. <paramref name="work"/> gets the
    /// resolved known folder of the student. Throws if nobody is signed in.
    /// </summary>
    public static T AsStudent<T>(Guid knownFolder, Func<string, T> work)
    {
        if (!RunningAsSystem)
            return work(KnownFolder(knownFolder, IntPtr.Zero));

        IntPtr userToken = IntPtr.Zero, impersonation = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(CurrentSessionId, out userToken))
                throw new InvalidOperationException("No student is signed in on this PC.");
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenImpersonation, out impersonation))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
            var folder = KnownFolder(knownFolder, impersonation);
            using var handle = new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(impersonation);
            impersonation = IntPtr.Zero;   // owned by the safe handle now
            return WindowsIdentity.RunImpersonated(handle, () => work(folder));
        }
        finally
        {
            if (impersonation != IntPtr.Zero) CloseHandle(impersonation);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    /// <summary>Open a folder in the student's Explorer (as the student).</summary>
    public static void OpenFolder(string path, ILogger logger)
    {
        if (!RunningAsSystem)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
            return;
        }
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        StartAsUser(explorer, $"\"{explorer}\" \"{path}\"", logger);
    }

    /// <summary>Open the student's Explorer on the folder holding <paramref name="file"/>, with it selected (as the student).</summary>
    public static void ShowFileInFolder(string file, ILogger logger)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!RunningAsSystem)
        {
            Process.Start(new ProcessStartInfo(explorer, $"/select,\"{file}\"") { UseShellExecute = false })?.Dispose();
            return;
        }
        StartAsUser(explorer, $"\"{explorer}\" /select,\"{file}\"", logger);
    }

    private static string KnownFolder(Guid id, IntPtr token)
    {
        var hr = SHGetKnownFolderPath(id, 0, token, out var ptr);
        try
        {
            if (hr != 0 || ptr == IntPtr.Zero) throw new Win32Exception(hr, "SHGetKnownFolderPath");
            return Marshal.PtrToStringUni(ptr)!;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    private const int TokenImpersonation = 2;

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
