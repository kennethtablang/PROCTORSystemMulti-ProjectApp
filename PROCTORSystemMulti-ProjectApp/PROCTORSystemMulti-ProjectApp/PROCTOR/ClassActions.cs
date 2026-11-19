using PROCTOR.Shared.Contracts;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// The teacher's classroom-control actions (P5), shared by the dashboard (whole class, targets =
/// null) and the focus view (one student). Each action prompts as needed, sends the command and
/// reports any refusal from the server (e.g. an invalid web address).
/// </summary>
public static class ClassActions
{
    /// <summary>Lock with the teacher's lock-screen look (Settings → Lock screen).</summary>
    public static Task LockAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets) =>
        Send(page, vm, CommandKind.Lock, targets, text: LockLook.Message, title: LockLook.Title, color: LockLook.Color);

    public static Task UnlockAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets) =>
        Send(page, vm, CommandKind.Unlock, targets);

    public static async Task MessageAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets, string who)
    {
        var text = await page.DisplayPromptAsync($"Message {who}", "What should the students see?",
            "Send", "Cancel", "e.g. Please save your work and look at the board", ClassroomLimits.MaxTextLength);
        if (string.IsNullOrWhiteSpace(text)) return;
        await Send(page, vm, CommandKind.Message, targets, text: text);
    }

    public static async Task OpenSiteAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets, string who)
    {
        var url = await page.DisplayPromptAsync($"Open a web page for {who}", "Web address (http or https):",
            "Open", "Cancel", "e.g. docs.google.com", ClassroomLimits.MaxUrlLength, Keyboard.Url);
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!ClassroomLimits.TryCleanUrl(url, out var clean))
        {
            await page.DisplayAlertAsync("Can't open that", "Only http:// or https:// web addresses can be opened.", "OK");
            return;
        }
        await Send(page, vm, CommandKind.OpenUrl, targets, url: clean);
    }

    public static async Task PowerAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets, string who)
    {
        const string logOff = "Log off", restart = "Restart", shutDown = "Shut down";
        // Whole class: also offer to switch on the lab PCs that are off (Wake-on-LAN).
        var offline = targets is null ? vm.OfflineComputers() : [];
        var wake = $"Switch on offline computers ({offline.Count})";
        var options = offline.Count > 0 ? new[] { wake, logOff, restart, shutDown } : new[] { logOff, restart, shutDown };
        var choice = await page.DisplayActionSheetAsync($"Power — {who}", "Cancel", null, options);
        if (choice == wake)
        {
            await WakeAsync(page, vm, offline);
            return;
        }
        var kind = choice switch
        {
            logOff => CommandKind.LogOff,
            restart => CommandKind.Restart,
            shutDown => CommandKind.ShutDown,
            _ => (CommandKind?)null,
        };
        if (kind is null) return;

        var warn = kind == CommandKind.LogOff
            ? "Students will be signed out of Windows. Unsaved work may be lost."
            : $"Students get a {ClassroomLimits.PowerCountdownSeconds}-second warning to save their work.";
        if (!await page.DisplayAlertAsync($"{choice} {who}?", warn, choice, "Cancel")) return;
        await Send(page, vm, kind.Value, targets);
    }

    /// <summary>Open a program on the students' PCs, or close all their programs (null targets = whole class).</summary>
    public static async Task ProgramsAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets, string who)
    {
        const string word = "Open Word", excel = "Open Excel", powerpoint = "Open PowerPoint", chrome = "Open Chrome",
            edge = "Open Edge", notepad = "Open Notepad", other = "Open another program…", closeAll = "Close all programs";
        var choice = await page.DisplayActionSheetAsync($"Programs — {who}", "Cancel", null,
            word, excel, powerpoint, chrome, edge, notepad, other, closeAll);
        string? program = choice switch
        {
            word => "Word", excel => "Excel", powerpoint => "PowerPoint", chrome => "Chrome", edge => "Edge", notepad => "Notepad",
            _ => null,
        };
        if (choice == other)
        {
            program = await page.DisplayPromptAsync($"Open a program for {who}",
                "Program name (e.g. Word, chrome.exe, code) or the full path of an .exe:", "Open", "Cancel",
                @"e.g. C:\Program Files\App\app.exe", ClassroomLimits.MaxProgramLength);
            if (string.IsNullOrWhiteSpace(program)) return;
            if (ClassroomLimits.CleanProgram(program) is null)
            {
                await page.DisplayAlertAsync("Can't open that", "Give just a program name or the full path of an .exe, without options or symbols.", "OK");
                return;
            }
        }
        if (program is not null)
        {
            await Send(page, vm, CommandKind.LaunchProgram, targets, text: program);
            return;
        }
        if (choice == closeAll && await page.DisplayAlertAsync($"Close all programs on {who}?",
                "Every open program is asked to close, like clicking its X. Programs with unsaved work ask the student whether to save.",
                "Close programs", "Cancel"))
            await Send(page, vm, CommandKind.CloseAllPrograms, targets);
    }

    /// <summary>Pick files and send them to the students (null targets = whole class).</summary>
    public static async Task SendFilesAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets, string who)
    {
        if (!vm.IsConnected)
        {
            await page.DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }
        IEnumerable<Microsoft.Maui.Storage.FileResult>? files;
        try { files = await Microsoft.Maui.Storage.FilePicker.Default.PickMultipleAsync(new Microsoft.Maui.Storage.PickOptions { PickerTitle = $"Send files to {who}" }); }
        catch (Exception ex) { await page.DisplayAlertAsync("Couldn't open the file picker", ex.Message, "OK"); return; }
        var list = files?.Where(f => f is not null).ToList();
        if (list is not { Count: > 0 }) return;
        var names = string.Join(", ", list.Take(5).Select(f => f.FileName)) + (list.Count > 5 ? $" and {list.Count - 5} more" : "");
        // Saved in "Files from teacher" on the desktop either way; opening shows it to the student.
        const string open = "Send and open the folder on their PCs", only = "Just send (students open it themselves)";
        var choice = await page.DisplayActionSheetAsync(
            $"Send {names} to {who}? It's saved in \u201CFiles from teacher\u201D on the desktop.", "Cancel", null, open, only);
        if (choice != open && choice != only) return;
        var error = await vm.SendFilesAsync(list, targets, openOnArrival: choice == open);
        if (error is not null) await page.DisplayAlertAsync("Couldn't send", error, "OK");
    }

    private static async Task WakeAsync(Page page, DashboardViewModel vm, IReadOnlyList<KnownComputer> offline)
    {
        if (!vm.IsConnected)
        {
            await page.DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }
        var names = string.Join(", ", offline.Take(8).Select(c => c.MachineName)) + (offline.Count > 8 ? $" and {offline.Count - 8} more" : "");
        if (!await page.DisplayAlertAsync("Switch on offline computers?", names, "Switch on", "Cancel")) return;
        var (count, error) = await vm.WakeAsync(offline);
        await page.DisplayAlertAsync(error is null ? "Wake-up sent" : "Couldn't wake computers",
            error ?? $"Sent a wake-up to {count} computer(s). They usually appear within a minute or two."
                     + " (Wake-on-LAN must be enabled in each PC's BIOS/network settings.)", "OK");
    }

    private static async Task Send(Page page, DashboardViewModel vm, CommandKind kind, IReadOnlyList<string>? targets,
        string? text = null, string? url = null, string? title = null, string? color = null)
    {
        if (!vm.IsConnected)
        {
            await page.DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }
        var error = await vm.SendCommandAsync(kind, targets, text, url, title, color);
        if (error is not null) await page.DisplayAlertAsync("Couldn't send", error, "OK");
    }
}
