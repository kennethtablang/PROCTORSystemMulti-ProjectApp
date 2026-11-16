using System.ComponentModel;
using ClassInsight.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.ClassInsight;

namespace PROCTORSystemMulti_ProjectApp;

public partial class DashboardPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public DashboardPage(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // Grid density (FR-TEACH-06). GridItemsLayout isn't in the visual tree, so it can't bind
        // to the page's context; drive its Span from the view model instead.
        ApplyColumns();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Columns)) ApplyColumns();
    }

    private void ApplyColumns()
    {
        if (TileGrid.ItemsLayout is GridItemsLayout layout) layout.Span = _vm.Columns;
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

    // ---- more tools: timer, random pick, snapshots, attendance, PIN ----
    private async void OnMore(object? sender, EventArgs e)
    {
        const string timer = "⏱ Lesson timer…", pick = "🎲 Pick a random student", snap = "📷 Snapshot all screens",
            attendance = "📋 Export attendance (CSV)", pin = "🔐 Teacher PIN…", lockNow = "🔒 Lock this app now",
            send = "📁 Send files to the class…", collect = "📥 Collect work…", stopCollect = "📥 Stop collecting work";
        var options = new List<string> { timer, send, _vm.IsCollecting ? stopCollect : collect, pick, snap, attendance, pin };
        if (TeacherPin.IsSet) options.Add(lockNow);
        switch (await DisplayActionSheetAsync("Teacher tools", "Cancel", null, options.ToArray()))
        {
            case timer: await StartTimer(); break;
            case pick: await PickStudent(); break;
            case snap: await SnapshotAll(); break;
            case attendance: await ExportAttendance(); break;
            case pin: await ManagePin(); break;
            case send: await ClassActions.SendFilesAsync(this, _vm, null, "the whole class"); break;
            case collect: await Collect(); break;
            case stopCollect: await _vm.StopCollectingAsync(); break;
            case lockNow: await Navigation.PushModalAsync(new LockPage()); break;
        }
    }

    private async Task Collect()
    {
        var prompt = await DisplayPromptAsync("Collect work", "What should students hand in? (shown to them)", "Start collecting", "Cancel",
            "e.g. Your finished worksheet", global::ClassInsight.Shared.Contracts.ClassroomLimits.MaxTextLength);
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
        if (await DisplayAlertAsync("🎲 Picked", tile.Header, "Tell them", "OK"))
            await _vm.SendCommandAsync(CommandKind.Message, [tile.ConnectionId], "🎲 You've been picked — your teacher would like to hear from you!");
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

    private async Task ManagePin()
    {
        if (TeacherPin.IsSet)
        {
            var choice = await DisplayActionSheetAsync("Teacher PIN", "Cancel", null, "Change PIN", "Remove PIN");
            if (choice is null or "Cancel") return;
            var current = await DisplayPromptAsync("Teacher PIN", "Current PIN:", "OK", "Cancel", maxLength: TeacherPin.MaxLength, keyboard: Keyboard.Numeric);
            if (current is null) return;
            if (!await TeacherPin.VerifyAsync(current)) { await DisplayAlertAsync("Teacher PIN", "That PIN isn't right.", "OK"); return; }
            if (choice == "Remove PIN") { TeacherPin.Clear(); await DisplayAlertAsync("Teacher PIN", "PIN removed.", "OK"); return; }
        }
        var pin = await DisplayPromptAsync("Teacher PIN", $"New PIN ({TeacherPin.MinLength}–{TeacherPin.MaxLength} digits). It's asked when the app opens and when you lock it.", "Next", "Cancel", maxLength: TeacherPin.MaxLength, keyboard: Keyboard.Numeric);
        if (pin is null) return;
        if (!TeacherPin.IsValidFormat(pin)) { await DisplayAlertAsync("Teacher PIN", $"Use {TeacherPin.MinLength}–{TeacherPin.MaxLength} digits.", "OK"); return; }
        var again = await DisplayPromptAsync("Teacher PIN", "Type it again:", "Save", "Cancel", maxLength: TeacherPin.MaxLength, keyboard: Keyboard.Numeric);
        if (again != pin) { await DisplayAlertAsync("Teacher PIN", "The PINs didn't match.", "OK"); return; }
        TeacherPin.Set(pin);
        await DisplayAlertAsync("Teacher PIN", "PIN saved. Use \"Lock this app now\" when you step away.", "OK");
    }

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
