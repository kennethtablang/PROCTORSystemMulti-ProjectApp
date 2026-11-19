using System.Diagnostics;
using System.Runtime.Versioning;
using PROCTOR.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent.Services;

/// <summary>
/// "Close all programs" (end of class): every program with a window in the student's session is
/// asked to close, the same as clicking its X, so programs with unsaved work still ask "Save?".
/// Windows itself, the shell and PROCTOR are never touched (<see cref="ClassroomLimits.ProtectedApps"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProgramCloser
{
    public static int CloseAll(ILogger logger)
    {
        NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var mySession);
        var asked = 0;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.SessionId != mySession) continue;
                    if (ClassroomLimits.ProtectedApps.Contains(p.ProcessName)) continue;
                    if (p.ProcessName.StartsWith("PROCTOR", StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;   // background helpers, tray apps
                    if (p.CloseMainWindow()) asked++;
                }
                catch { /* exited, or access denied */ }
            }
        }
        logger.LogInformation("Asked {Count} program(s) to close (teacher's \"close all programs\").", asked);
        return asked;
    }
}
