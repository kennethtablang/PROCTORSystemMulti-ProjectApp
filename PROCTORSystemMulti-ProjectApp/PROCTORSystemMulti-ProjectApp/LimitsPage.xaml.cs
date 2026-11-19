using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>Edit the room's program / internet / sound limits (P5). Websites have their own window.</summary>
public partial class LimitsPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public LimitsPage(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        var p = vm.Policy;
        Apps.Text = string.Join(Environment.NewLine, p.BlockedApps);
        BlockInternet.IsToggled = p.BlockInternet;
        Mute.IsToggled = p.MuteSound;
        BlockUsb.IsToggled = p.BlockUsbStorage;
    }

    private async void OnApply(object? sender, EventArgs e)
    {
        // The website part and a running exam are left as they are (their own windows set them).
        var policy = new ClassPolicy
        {
            BlockedApps = Lines(Apps.Text),
            BlockInternet = BlockInternet.IsToggled,
            MuteSound = Mute.IsToggled,
            BlockUsbStorage = BlockUsb.IsToggled,
            ExamMode = _vm.Policy.ExamMode,
            ExamTitle = _vm.Policy.ExamTitle,
        }.WithWeb(_vm.Policy.Web);

        // Catch typos before sending: anything the server would drop is reported here.
        var badApps = policy.BlockedApps.Where(a => ClassroomLimits.CleanAppName(a) is null).ToArray();
        if (badApps.Length > 0)
        {
            ShowError($"Can't block: {string.Join(", ", badApps)} (not a program name, or protected).");
            return;
        }

        await Save(policy);
    }

    private async void OnClear(object? sender, EventArgs e) =>
        await Save(new ClassPolicy { ExamMode = _vm.Policy.ExamMode, ExamTitle = _vm.Policy.ExamTitle }.WithWeb(_vm.Policy.Web));

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
