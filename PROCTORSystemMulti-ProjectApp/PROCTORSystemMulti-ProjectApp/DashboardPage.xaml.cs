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
        Application.Current!.Resources[TileHeightKey] = 140d + TileExtraHeight;
        GridScroller.SizeChanged += (_, _) => ApplyTileSize();
        SizeChanged += (_, _) => ApplyToolbarLayout();
        _vm.PropertyChanged += OnVmPropertyChanged;

        // Phones and tablets can't open a folder: the button shares the handed-in files instead.
        if (!OperatingSystem.IsWindows()) OpenCollectButton.Text = "Share work";

        UpdateThemeButton();
        Application.Current.RequestedThemeChanged += (_, _) => UpdateThemeButton();
    }

    // ---- responsive toolbar ----
    // Below this width (phones, portrait tablets, a narrow window) the view controls get their own
    // row under the class actions instead of a column beside them.
    private const double NarrowToolbar = 1000;
    private bool? _narrowToolbar;

    private void ApplyToolbarLayout()
    {
        if (Width <= 0) return;
        var narrow = Width < NarrowToolbar;
        if (narrow == _narrowToolbar) return;
        _narrowToolbar = narrow;
        Grid.SetRow(ViewControls, narrow ? 1 : 0);
        Grid.SetColumn(ViewControls, narrow ? 0 : 1);
        Grid.SetColumnSpan(ViewControls, narrow ? 2 : 1);
    }

    // ---- light / dark mode ----
    private void OnToggleTheme(object? sender, EventArgs e) => Appearance.Toggle();

    private void UpdateThemeButton()
    {
        var dark = Appearance.IsDark;
        ThemeButton.Glyph = dark ? Icons.LightMode : Icons.DarkMode;
        ThemeButton.ToolTip = dark ? "Switch to light mode" : "Switch to dark mode";
    }

    private const string ThumbHeightKey = "TileThumbHeight";
    private const string TileHeightKey = "TileHeight";
    private const double TileSpacing = 10;
    private const double TileChrome = 2 * 8 + 4;   // tile padding + border
    // Everything in a tile besides the picture: padding + border, name row, three info rows, spacing.
    private const double TileExtraHeight = 16 + 4 + 28 + 4 + 4 + 16 + 4 + 14 + 4 + 16;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.TileSize) or nameof(DashboardViewModel.LayoutMode)
            or nameof(DashboardViewModel.LayoutColumns))
            ApplyTileSize();
    }

    /// <summary>
    /// Columns: from the Size slider, or (lab layout) the computers per row. The picture keeps a
    /// 16:9 shape, and every tile and seat gets the same height so the rows line up.
    /// </summary>
    // A lab-layout seat is never narrower than this; wider rooms scroll sideways (phones).
    private const double MinSeatWidth = 170;

    private void ApplyTileSize()
    {
        var width = GridScroller.Width;
        if (width <= 0) return;
        var layoutMode = _vm.LayoutMode && _vm.LayoutColumns > 0;
        var span = layoutMode
            ? _vm.LayoutColumns
            : Math.Clamp((int)Math.Floor((width + TileSpacing) / (_vm.TileSize + TileSpacing)), 1, 12);
        if (!layoutMode) _vm.Columns = span;
        if (TileGrid.ItemsLayout is GridItemsLayout layout && layout.Span != span) layout.Span = span;
        var tileWidth = (width - TileSpacing * (span - 1)) / span;
        if (layoutMode) tileWidth = Math.Max(MinSeatWidth, tileWidth);
        var gridWidth = layoutMode ? tileWidth * span + TileSpacing * (span - 1) : width;
        if (Math.Abs(TileGrid.WidthRequest - gridWidth) > 0.5) TileGrid.WidthRequest = gridWidth;
        var thumb = Math.Max(60, Math.Round((tileWidth - TileChrome) * 9 / 16));
        Application.Current!.Resources[ThumbHeightKey] = thumb;
        Application.Current!.Resources[TileHeightKey] = thumb + TileExtraHeight;
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

    // ---- class controls (P5): the whole class, or only the selected students ----
    private async void OnLockAll(object? sender, EventArgs e) => await ClassActions.LockAsync(this, _vm, _vm.SelectedTargets);
    private async void OnUnlockAll(object? sender, EventArgs e) => await ClassActions.UnlockAsync(this, _vm, _vm.SelectedTargets);
    private async void OnMessageAll(object? sender, EventArgs e) => await ClassActions.MessageAsync(this, _vm, _vm.SelectedTargets, _vm.TargetsWho);
    private async void OnOpenSiteAll(object? sender, EventArgs e) => await ClassActions.OpenSiteAsync(this, _vm, _vm.SelectedTargets, _vm.TargetsWho);
    private async void OnPowerAll(object? sender, EventArgs e) =>
        await ClassActions.PowerAsync(this, _vm, _vm.SelectedTargets, _vm.HasSelection ? _vm.TargetsWho : "every student PC");
    private async void OnLimits(object? sender, EventArgs e) => await Navigation.PushModalAsync(new LimitsPage(_vm));
    private async void OnWebsites(object? sender, EventArgs e) => await Navigation.PushModalAsync(new WebsitesPage(_vm));
    private async void OnShowScreen(object? sender, EventArgs e)
    {
        if (!_vm.Broadcaster.IsSupported)
        {
            await DisplayAlertAsync("Not available here", "Showing your screen isn't available on this device.", "OK");
            return;
        }
        const string full = "Full screen (students can't close it)", window = "In a window";
        var choice = await DisplayActionSheetAsync($"Show your screen to {_vm.TargetsWho}", "Cancel", null, full, window);
        if (choice != full && choice != window) return;
        var error = await _vm.Broadcaster.StartAsync(choice == full ? BroadcastMode.FullScreen : BroadcastMode.Window, _vm.SelectedTargets);
        if (error is not null) await DisplayAlertAsync("Couldn't show your screen", error, "OK");
    }

    private async void OnStopShowScreen(object? sender, EventArgs e) => await _vm.Broadcaster.StopAsync();

    // ---- teacher tools (each its own button in the toolbar) ----
    private async void OnTimer(object? sender, EventArgs e) => await StartTimer();
    private async void OnPick(object? sender, EventArgs e) => await PickStudent();
    private async void OnSnapshot(object? sender, EventArgs e) => await SnapshotAll();
    private async void OnAttendance(object? sender, EventArgs e) => await ExportAttendance();
    private async void OnPin(object? sender, EventArgs e) => await ManagePin();
    private async void OnSendFiles(object? sender, EventArgs e) => await ClassActions.SendFilesAsync(this, _vm, _vm.SelectedTargets, _vm.TargetsWho);

    // ---- reload, selection, lab layout ----
    private async void OnReload(object? sender, EventArgs e)
    {
        var only = _vm.HasSelection ? _vm.SelectedStudents.Select(s => s.ConnectionId).ToArray() : null;
        await _vm.Streams.ReloadAsync(only);
    }

    private void OnSelectAll(object? sender, EventArgs e) => _vm.SelectAll();
    private void OnClearSelection(object? sender, EventArgs e) => _vm.ClearSelection();
    private void OnArrangeDone(object? sender, EventArgs e) => _vm.IsArranging = false;

    private async void OnLayout(object? sender, EventArgs e)
    {
        if (!_vm.HasLayout)
        {
            await CreateLayoutAsync();
            return;
        }
        const string show = "Show the lab layout", normal = "Back to the normal grid", arrange = "Rearrange seats (tap two to swap)",
            size = "Change rows and computers per row", auto = "Re-arrange by computer name", clear = "Delete this layout";
        var options = _vm.LayoutMode ? new[] { normal, arrange, size, auto, clear } : new[] { show, arrange, size, auto, clear };
        var choice = await DisplayActionSheetAsync($"Lab layout · {_vm.LayoutRows} rows × {_vm.LayoutColumns} computers", "Cancel", null, options);
        switch (choice)
        {
            case show: _vm.LayoutMode = true; break;
            case normal: _vm.LayoutMode = false; break;
            case arrange: _vm.LayoutMode = true; _vm.IsArranging = true; break;
            case size:
                if (await AskLayoutSizeAsync(_vm.LayoutRows, _vm.LayoutColumns) is { } s) { _vm.ResizeLayout(s.Rows, s.Columns); _vm.LayoutMode = true; }
                break;
            case auto:
                if (await DisplayAlertAsync("Re-arrange by computer name?", "Seats are filled in name order (PC1, PC2 … PC10). Your own arrangement is replaced.", "Re-arrange", "Cancel"))
                    _vm.AutoArrange();
                break;
            case clear:
                if (await DisplayAlertAsync("Delete the lab layout?", "The dashboard goes back to the normal grid. You can make a new layout any time.", "Delete", "Cancel"))
                    _vm.ClearLayout();
                break;
        }
        ApplyTileSize();
    }

    private async Task CreateLayoutAsync()
    {
        var count = Math.Max(1, _vm.StudentCount);
        var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count * 1.5)), 1, DashboardViewModel.MaxLayoutSide);
        var rows = Math.Clamp((int)Math.Ceiling(count / (double)columns), 1, DashboardViewModel.MaxLayoutSide);
        if (await AskLayoutSizeAsync(rows, columns) is not { } s) return;
        _vm.CreateLayout(s.Rows, s.Columns);
        ApplyTileSize();
        await DisplayAlertAsync("Lab layout ready",
            "Computers are seated in name order. To match the room, use the layout button → Rearrange seats, " +
            "then tap a computer and the seat it belongs in. The layout is saved on this device for this classroom.", "OK");
    }

    private async Task<(int Rows, int Columns)?> AskLayoutSizeAsync(int rows, int columns)
    {
        var r = await DisplayPromptAsync("Lab layout", "How many rows of computers are in the lab?", "Next", "Cancel",
            "e.g. 5", 2, Keyboard.Numeric, rows.ToString());
        if (!int.TryParse(r, out var rr) || rr < 1) return null;
        var c = await DisplayPromptAsync("Lab layout", "How many computers in each row? (count aisles as places too)", "OK", "Cancel",
            "e.g. 8", 2, Keyboard.Numeric, columns.ToString());
        if (!int.TryParse(c, out var cc) || cc < 1) return null;
        return (Math.Min(rr, DashboardViewModel.MaxLayoutSide), Math.Min(cc, DashboardViewModel.MaxLayoutSide));
    }

    private void OnTileHeaderTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not StudentTile tile) return;
        if (_vm.IsArranging) { _vm.SeatTapped(_vm.SeatIndexOf(tile)); return; }
        tile.IsSelected = !tile.IsSelected;
    }

    private void OnSeatTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is SeatSlot seat && _vm.IsArranging) _vm.SeatTapped(seat.Index);
    }

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
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            await TeacherFiles.ShowSavedAsync(this, "Handed-in work",
                files.Length == 0 ? "Nothing has been handed in yet." : $"{files.Length} file(s) handed in.", files);
        }
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
        if (count == 0 || folder is null) { await DisplayAlertAsync("Snapshot", "No screens to save yet.", "OK"); return; }
        await TeacherFiles.ShowSavedAsync(this, "Snapshot",
            OperatingSystem.IsWindows() ? $"Saved {count} screen(s) to:{Environment.NewLine}{folder}" : $"Saved {count} screen(s).",
            Directory.GetFiles(folder));
    }

    private async Task ExportAttendance()
    {
        if (_vm.AttendanceCount == 0)
        {
            await DisplayAlertAsync("Attendance", "No students have connected this session yet.", "OK");
            return;
        }
        var path = _vm.ExportAttendanceCsv();
        await TeacherFiles.ShowSavedAsync(this, "Attendance exported",
            OperatingSystem.IsWindows() ? path : "Saved the attendance list (CSV, opens in Excel or Sheets).", [path]);
    }

    private Task ManagePin() => TeacherPinFlow.ManageAsync(this);

    private async void OnPoll(object? sender, EventArgs e) => await Navigation.PushModalAsync(new PollPage(_vm));

    // A tile opens the focus view only on an explicit tap. (We deliberately don't use
    // CollectionView selection for this: on WinUI the list auto-selects its first item when the
    // window gains focus, which would pop open a student's full screen with no click.)
    private async void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not StudentTile tile) return;

        // Rearranging the lab: a tap picks this computer's seat instead of opening it.
        if (_vm.IsArranging)
        {
            _vm.SeatTapped(_vm.SeatIndexOf(tile));
            return;
        }

        if (!_vm.Client.IsConnected)
        {
            await DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }

        await Navigation.PushAsync(new FocusPage(tile, _vm));
    }
}
