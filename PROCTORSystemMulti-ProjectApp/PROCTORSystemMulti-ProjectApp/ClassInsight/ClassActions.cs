using ClassInsight.Shared.Contracts;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// The teacher's classroom-control actions (P5), shared by the dashboard (whole class, targets =
/// null) and the focus view (one student). Each action prompts as needed, sends the command and
/// reports any refusal from the server (e.g. an invalid web address).
/// </summary>
public static class ClassActions
{
    public static Task LockAsync(Page page, DashboardViewModel vm, IReadOnlyList<string>? targets) =>
        Send(page, vm, CommandKind.Lock, targets);

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
        var choice = await page.DisplayActionSheetAsync($"Power — {who}", "Cancel", null, logOff, restart, shutDown);
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

    private static async Task Send(Page page, DashboardViewModel vm, CommandKind kind, IReadOnlyList<string>? targets,
        string? text = null, string? url = null)
    {
        if (!vm.IsConnected)
        {
            await page.DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }
        var error = await vm.SendCommandAsync(kind, targets, text, url);
        if (error is not null) await page.DisplayAlertAsync("Couldn't send", error, "OK");
    }
}
