using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>Edit the room's web / app / internet / sound limits (P5, FR-CTRL-06).</summary>
public partial class LimitsPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public LimitsPage(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        var p = vm.Policy;
        WebMode.SelectedIndex = (int)p.WebMode;
        WebHosts.Text = string.Join(Environment.NewLine, p.WebHosts);
        Apps.Text = string.Join(Environment.NewLine, p.BlockedApps);
        BlockInternet.IsToggled = p.BlockInternet;
        Mute.IsToggled = p.MuteSound;
    }

    private async void OnApply(object? sender, EventArgs e)
    {
        var policy = new ClassPolicy
        {
            WebMode = (WebLimitMode)Math.Max(0, WebMode.SelectedIndex),
            WebHosts = Lines(WebHosts.Text),
            BlockedApps = Lines(Apps.Text),
            BlockInternet = BlockInternet.IsToggled,
            MuteSound = Mute.IsToggled,
        };

        // Catch typos before sending: anything the server would drop is reported here.
        var badHosts = policy.WebHosts.Where(h => ClassroomLimits.CleanHost(h) is null).ToArray();
        var badApps = policy.BlockedApps.Where(a => ClassroomLimits.CleanAppName(a) is null).ToArray();
        if (badHosts.Length > 0 || badApps.Length > 0)
        {
            ShowError(string.Join("  ",
                badHosts.Length > 0 ? $"Not a site: {string.Join(", ", badHosts)}." : "",
                badApps.Length > 0 ? $"Can't block: {string.Join(", ", badApps)} (not a program name, or protected)." : "").Trim());
            return;
        }
        if (policy.WebMode == WebLimitMode.AllowOnlyListed && policy.WebHosts.Count == 0
            && !await DisplayAlertAsync("Block every website?", "\"Allow only these sites\" with an empty list blocks all websites.", "Block all", "Cancel"))
            return;

        await Save(policy);
    }

    private async void OnClear(object? sender, EventArgs e) => await Save(new ClassPolicy());

    private async void OnCancel(object? sender, EventArgs e) => await Navigation.PopModalAsync();

    private async Task Save(ClassPolicy policy)
    {
        if (!_vm.IsConnected)
        {
            ShowError("Not connected to the classroom server.");
            return;
        }
        var error = await _vm.SetPolicyAsync(policy);
        if (error is not null) { ShowError(error); return; }
        await Navigation.PopModalAsync();
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.IsVisible = true;
    }

    private static string[] Lines(string? text) =>
        (text ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
