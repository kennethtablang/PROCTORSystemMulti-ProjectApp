using System.ComponentModel;
using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

public partial class DashboardPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public DashboardPage(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // Screen size (FR-TEACH-06): the Size slider sets how wide a student screen should be; the
        // columns follow from the grid's width, and the picture keeps a 16:9 shape.
        // GridItemsLayout isn't in the visual tree, so its Span is set here rather than bound.
        Application.Current!.Resources[ThumbHeightKey] = 140d;
        TileGrid.SizeChanged += (_, _) => ApplyTileSize();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private const string ThumbHeightKey = "TileThumbHeight";
    private const double TileSpacing = 10;
    private const double TileChrome = 2 * 8 + 4;   // tile padding + border

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.TileSize)) ApplyTileSize();
    }

    private void ApplyTileSize()
    {
        var width = TileGrid.Width;
        if (width <= 0) return;
        var span = Math.Clamp((int)Math.Floor((width + TileSpacing) / (_vm.TileSize + TileSpacing)), 1, 12);
        _vm.Columns = span;
        if (TileGrid.ItemsLayout is GridItemsLayout layout && layout.Span != span) layout.Span = span;
        var tileWidth = (width - TileSpacing * (span - 1)) / span;
        Application.Current!.Resources[ThumbHeightKey] = Math.Max(60, Math.Round((tileWidth - TileChrome) * 9 / 16));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Thumbnails stream only while the grid is on screen (encode only when viewed, R-3).
        _vm.Streams.SetGridVisible(true);
        // "Just open the app" — try to connect with no code as soon as the grid shows.
        await _vm.AutoConnectAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Streams.SetGridVisible(false);
    }

    // ---- whole-class controls (P5) ----
    private async void OnLockAll(object? sender, EventArgs e) => await ClassActions.LockAsync(this, _vm, null);
    private async void OnUnlockAll(object? sender, EventArgs e) => await ClassActions.UnlockAsync(this, _vm, null);
    private async void OnMessageAll(object? sender, EventArgs e) => await ClassActions.MessageAsync(this, _vm, null, "the whole class");
    private async void OnOpenSiteAll(object? sender, EventArgs e) => await ClassActions.OpenSiteAsync(this, _vm, null, "the whole class");
    private async void OnPowerAll(object? sender, EventArgs e) => await ClassActions.PowerAsync(this, _vm, null, "every student PC");
    private async void OnLimits(object? sender, EventArgs e) => await Navigation.PushModalAsync(new LimitsPage(_vm));
    private async void OnWebsites(object? sender, EventArgs e) => await Navigation.PushModalAsync(new WebsitesPage(_vm));
    private async void OnShowScreen(object? sender, EventArgs e)
    {
        if (!_vm.Broadcaster.IsSupported)
        {
            await DisplayAlertAsync("Not available here", "Showing your screen is available on the Windows teacher app.", "OK");
            return;
        }
        const string full = "Full screen (students can't close it)", window = "In a window";
        var choice = await DisplayActionSheetAsync("Show your screen to the whole class", "Cancel", null, full, window);
        if (choice != full && choice != window) return;
        var error = await _vm.Broadcaster.StartAsync(choice == full ? BroadcastMode.FullScreen : BroadcastMode.Window);
        if (error is not null) await DisplayAlertAsync("Couldn't show your screen", error, "OK");
    }

    private async void OnStopShowScreen(object? sender, EventArgs e) => await _vm.Broadcaster.StopAsync();

    // ---- teacher tools (each its own button in the toolbar) ----
    private async void OnTimer(object? sender, EventArgs e) => await StartTimer();
    private async void OnPick(object? sender, EventArgs e) => await PickStudent();
    private async void OnSnapshot(object? sender, EventArgs e) => await SnapshotAll();
    private async void OnAttendance(object? sender, EventArgs e) => await ExportAttendance();
    private async void OnPin(object? sender, EventArgs e) => await ManagePin();
    private async void OnSendFiles(object? sender, EventArgs e) => await ClassActions.SendFilesAsync(this, _vm, null, "the whole class");

    private async void OnCollectToggle(object? sender, EventArgs e)
    {
        if (_vm.IsCollecting) await _vm.StopCollectingAsync();
        else await Collect();
    }

    private async void OnLockApp(object? sender, EventArgs e) => await TeacherPinFlow.LockNowAsync(this);

    private async void OnSettings(object? sender, EventArgs e)
    {
        if (Navigation.ModalStack.LastOrDefault() is SettingsPage) return;
        await Navigation.PushModalAsync(new SettingsPage(_vm));
    }

    private async Task Collect()
    {
        var prompt = await DisplayPromptAsync("Collect work", "What should students hand in? (shown to them)", "Start collecting", "Cancel",
            "e.g. Your finished worksheet", global::PROCTOR.Shared.Contracts.ClassroomLimits.MaxTextLength);
        if (prompt is null) return;
        var error = await _vm.StartCollectingAsync(prompt);
        if (error is not null) await DisplayAlertAsync("Couldn't start collecting", error, "OK");
    }

    private async void OnStopCollecting(object? sender, EventArgs e) => await _vm.StopCollectingAsync();

    private async void OnOpenCollectFolder(object? sender, EventArgs e)
    {
        if (_vm.CollectFolder is not { } folder) return;
        Directory.CreateDirectory(folder);
        // This shared project targets plain net10.0, so test at run time rather than with #if WINDOWS.
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
        else
            await DisplayAlertAsync("Handed-in work", folder, "OK");
    }

    private async Task StartTimer()
    {
        var minutes = await DisplayPromptAsync("Lesson timer", "Minutes (students see the countdown):", "Next", "Cancel", "e.g. 10", 4, Keyboard.Numeric, "10");
        if (!double.TryParse(minutes, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var m) || m <= 0) return;
        var label = await DisplayPromptAsync("Lesson timer", "Label (optional):", "Start", "Cancel", "e.g. Finish the worksheet", TimerLimits.MaxLabelLength);
        if (label is null) return;
        var error = await _vm.StartTimerAsync((int)Math.Round(m * 60), label);
        if (error is not null) await DisplayAlertAsync("Couldn't start the timer", error, "OK");
    }

    private async void OnStopTimer(object? sender, EventArgs e) => await _vm.StopTimerAsync();

    private async Task PickStudent()
    {
        if (_vm.PickRandomStudent() is not { } tile)
        {
            await DisplayAlertAsync("Pick a student", "No students are connected.", "OK");
            return;
        }
        if (await DisplayAlertAsync("Picked", tile.Header, "Tell them", "OK"))
            await _vm.SendCommandAsync(CommandKind.Message, [tile.ConnectionId], "You've been picked — your teacher would like to hear from you!");
    }

    private async Task SnapshotAll()
    {
        var (folder, count) = _vm.SaveAllThumbnails();
        await DisplayAlertAsync("Snapshot", count == 0 ? "No screens to save yet." : $"Saved {count} screen(s) to:{Environment.NewLine}{folder}", "OK");
    }

    private async Task ExportAttendance()
    {
        if (_vm.AttendanceCount == 0)
        {
            await DisplayAlertAsync("Attendance", "No students have connected this session yet.", "OK");
            return;
        }
        var path = _vm.ExportAttendanceCsv();
        await DisplayAlertAsync("Attendance exported", path, "OK");
    }

    private Task ManagePin() => TeacherPinFlow.ManageAsync(this);

    private async void OnPoll(object? sender, EventArgs e) => await Navigation.PushModalAsync(new PollPage(_vm));

    // A tile opens the focus view only on an explicit tap. (We deliberately don't use
    // CollectionView selection for this: on WinUI the list auto-selects its first item when the
    // window gains focus, which would pop open a student's full screen with no click.)
    private async void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not StudentTile tile) return;

        if (!_vm.Client.IsConnected)
        {
            await DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }

        await Navigation.PushAsync(new FocusPage(tile, _vm));
    }
}
