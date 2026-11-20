using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Focus view (FR-TEACH-02): one student's screen at focus quality, live, beside their telemetry.
/// The stream itself is owned by the shared <see cref="StreamManager"/>: appearing asks it to
/// upgrade this student to <c>Focus</c> (on the chosen display), leaving hands the student back
/// to thumbnail/none, so the agent only encodes what is on screen (FR-STREAM-04/06).
/// </summary>
public partial class FocusPage : ContentPage
{
    private const double NarrowWidth = 760;
    private const string PrefMetrics = "proctor.focus.metrics";

    private readonly StudentTile _tile;
    private readonly DashboardViewModel _vm;
    private int _display;
    private readonly IDispatcherTimer _infoTimer;
    private bool? _narrow;

    public FocusPage(StudentTile tile, DashboardViewModel vm)
    {
        InitializeComponent();
        _tile = tile;
        _vm = vm;

        Title = tile.Header;
        // Everything on the page binds to the live tile, which the dashboard keeps updating from
        // the telemetry stream, and whose frame buffer the focus stream paints into.
        BindingContext = tile;
        if (tile.HasMultipleDisplays) DisplayPicker.SelectedIndex = 0;

        ResolutionPicker.ItemsSource = vm.FocusChoices.ToList();
        ResolutionPicker.SelectedIndex = vm.FocusResolutionIndex;

        // "Live · 1280×720": the resolution actually being received, refreshed as frames arrive.
        _infoTimer = Dispatcher.CreateTimer();
        _infoTimer.Interval = TimeSpan.FromMilliseconds(500);
        _infoTimer.Tick += (_, _) =>
        {
            var f = _tile.Frame;
            StreamInfo.Text = f.HasFrame && _tile.StreamStatus == "Live"
                ? $"Live · {f.Width}×{f.Height}"
                : _tile.StreamStatus;
        };

        SizeChanged += (_, _) => ApplyLayout();
        ApplyMetrics(Preferences.Default.Get(PrefMetrics, true));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_vm.Client.IsConnected)
        {
            _tile.StreamStatus = "Not connected to a server.";
            return;
        }
        _vm.Streams.SetFocus(_tile.ConnectionId, _display);
        _infoTimer.Start();
        _vm.Client.RemoteControlEnded += OnRemoteControlEnded;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _infoTimer.Stop();
        _vm.Streams.SetFocus(null);
        _vm.Client.RemoteControlEnded -= OnRemoteControlEnded;
        if (_control is not null) _ = StopControlAsync(null);   // leaving the view always hands control back
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (DisplayPicker.SelectedIndex < 0 || DisplayPicker.SelectedIndex == _display) return;
        _display = DisplayPicker.SelectedIndex;
        _vm.Streams.SetFocus(_tile.ConnectionId, _display);
    }

    private void OnResolutionChanged(object? sender, EventArgs e)
    {
        if (ResolutionPicker.SelectedIndex >= 0) _vm.FocusResolutionIndex = ResolutionPicker.SelectedIndex;
    }

    private async void OnStopClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

    // ---- per-student classroom controls (P5) ----
    private IReadOnlyList<string> Me => [_tile.ConnectionId];
    private async void OnLock(object? sender, EventArgs e) => await ClassActions.LockAsync(this, _vm, Me);
    private async void OnUnlock(object? sender, EventArgs e) => await ClassActions.UnlockAsync(this, _vm, Me);
    private async void OnMessage(object? sender, EventArgs e) => await ClassActions.MessageAsync(this, _vm, Me, _tile.MachineName);
    private async void OnOpenSite(object? sender, EventArgs e) => await ClassActions.OpenSiteAsync(this, _vm, Me, _tile.MachineName);
    private async void OnPower(object? sender, EventArgs e) => await ClassActions.PowerAsync(this, _vm, Me, _tile.MachineName);
    private async void OnLowerHand(object? sender, EventArgs e) => await _vm.LowerHandAsync(_tile);

    // ---- remote control (FR-CTRL-05) ----
    private RemoteControlSession? _control;
    private IDisposable? _capture;

    private async void OnControl(object? sender, EventArgs e)
    {
        if (_control is not null) { await StopControlAsync(null); return; }
        var input = PlatformServices.RemoteInput;
        if (!input.IsSupported)
        {
            await DisplayAlertAsync("Not available here", "Remote control isn't available on this device.", "OK");
            return;
        }
        try
        {
            _control = await RemoteControlSession.StartAsync(_vm.Client, _tile.ConnectionId, _display);
            _control.Failed += reason => MainThread.BeginInvokeOnMainThread(async () => await StopControlAsync("Control ended: " + reason));
            _capture = input.Attach(Screen, ToFrame, _control.Post);
            // Your mouse (over the picture) and keyboard now work this PC; the student sees a red
            // "your teacher is controlling this computer" banner. Click the button again to stop.
            ControlButton.Glyph = Icons.Stop;
            ControlButton.ToolTip = "Stop control";
            ControlButton.BackgroundColor = Color.FromArgb("#C62828");
            ControlButton.IconColor = Colors.White;
            KeyboardButton.IsVisible = input.HasOnScreenKeyboard;
            if (input.HasOnScreenKeyboard)
                await DisplayAlertAsync("You're in control",
                    "Tap = click · hold = right-click · drag = drag · two fingers = scroll.\nThe keyboard button types on the student's PC.", "OK");
        }
        catch (Exception ex)
        {
            await StopControlAsync(null);
            await DisplayAlertAsync("Couldn't take control", ex.Message, "OK");
        }
    }

    private async Task StopControlAsync(string? message)
    {
        _capture?.Dispose();
        _capture = null;
        var session = _control;
        _control = null;
        if (session is not null) await session.StopAsync();
        ControlButton.Glyph = Icons.Control;
        ControlButton.ToolTip = "Take control (mouse and keyboard)";
        ControlButton.ClearValue(Button.BackgroundColorProperty);
        ControlButton.ClearValue(IconButton.IconColorProperty);
        KeyboardButton.IsVisible = false;
        if (message is not null) await DisplayAlertAsync("Remote control", message, "OK");
    }

    private async void OnReload(object? sender, EventArgs e) => await _vm.Streams.ReloadAsync([_tile.ConnectionId]);

    private async void OnPrograms(object? sender, EventArgs e) =>
        await ClassActions.ProgramsAsync(this, _vm, [_tile.ConnectionId], _tile.MachineName);

    private void OnKeyboard(object? sender, EventArgs e)
    {
        if (_control is not null) PlatformServices.RemoteInput.ShowKeyboard();
    }

    private void OnRemoteControlEnded(string student)
    {
        if (student == _tile.ConnectionId && _control is not null)
            MainThread.BeginInvokeOnMainThread(async () => await StopControlAsync("Control ended: another teacher took over, or the student disconnected."));
    }

    /// <summary>View point → normalised point on the student's screen (the picture is letterboxed).</summary>
    private Point? ToFrame(Point p)
    {
        double fw = _tile.Frame.Width, fh = _tile.Frame.Height, vw = Screen.Width, vh = Screen.Height;
        if (fw <= 0 || fh <= 0 || vw <= 0 || vh <= 0) return null;
        var scale = Math.Min(vw / fw, vh / fh);
        double w = fw * scale, h = fh * scale;
        var x = (p.X - (vw - w) / 2) / w;
        var y = (p.Y - (vh - h) / 2) / h;
        return x is < 0 or > 1 || y is < 0 or > 1 ? null : new Point(x, y);
    }

    private async void OnSendFile(object? sender, EventArgs e) =>
        await ClassActions.SendFilesAsync(this, _vm, [_tile.ConnectionId], _tile.MachineName);

    private async void OnChat(object? sender, EventArgs e) => await Navigation.PushModalAsync(new ChatPage(_vm, _tile));

    // Show this student's screen to the rest of the class (relayed through this app).
    private async void OnShowToClass(object? sender, EventArgs e)
    {
        if (_vm.Broadcaster.IsActive && _vm.Broadcaster.ShowingStudent?.ConnectionId == _tile.ConnectionId)
        {
            if (await DisplayAlertAsync("Showing to the class", $"Stop showing {_tile.MachineName}'s screen?", "Stop", "Keep showing"))
                await _vm.Broadcaster.StopAsync();
            return;
        }
        const string full = "Full screen (students can't close it)", window = "In a window";
        var choice = await DisplayActionSheetAsync($"Show {_tile.MachineName}'s screen to the class", "Cancel", null, full, window);
        if (choice != full && choice != window) return;
        var error = await _vm.Broadcaster.StartAsync(choice == full ? global::PROCTOR.Shared.Contracts.BroadcastMode.FullScreen : global::PROCTOR.Shared.Contracts.BroadcastMode.Window, null, _tile);
        await DisplayAlertAsync(error is null ? "Showing to the class" : "Couldn't show this screen",
            error ?? $"The class now sees {_tile.MachineName}'s screen, and the student has been told. Stop it from the dashboard banner or this button.", "OK");
    }

    private async void OnHistory(object? sender, EventArgs e) => await Navigation.PushModalAsync(new HistoryPage(_vm, _tile.MachineName));

    private async void OnScreenshot(object? sender, EventArgs e)
    {
        var path = DashboardViewModel.SaveScreenshot(_tile);
        if (path is null) { await DisplayAlertAsync("Screenshot", "There's no picture yet.", "OK"); return; }
        await TeacherFiles.ShowSavedAsync(this, "Screenshot",
            OperatingSystem.IsWindows() ? $"Saved to:\n{path}" : $"Saved {_tile.MachineName}'s screen.", [path]);
    }

    private async void OnWebsites(object? sender, EventArgs e) => await Navigation.PushModalAsync(new WebsitesPage(_vm, _tile));

    // Live metrics panel: collapsible; the screen takes the freed space (its column/row is Auto).
    private void OnToggleMetrics(object? sender, EventArgs e)
    {
        var show = !DetailPanel.IsVisible;
        Preferences.Default.Set(PrefMetrics, show);
        ApplyMetrics(show);
    }

    private void ApplyMetrics(bool show)
    {
        DetailPanel.IsVisible = show;
        MetricsButton.ToolTip = show ? "Hide live metrics" : "Show live metrics";
        MetricsButton.BackgroundColor = Color.FromArgb(show ? "#2A2D34" : "#4A3B6B");
    }

    /// <summary>Side panel on wide windows; panel stacked under the video on phones (NFR-USE-03).</summary>
    private void ApplyLayout()
    {
        if (Width <= 0) return;
        var narrow = Width < NarrowWidth;
        if (narrow == _narrow) return;
        _narrow = narrow;

        // Header: the pickers go on their own line under the name, sized to the screen.
        Grid.SetRow(Pickers, narrow ? 1 : 0);
        Grid.SetColumn(Pickers, narrow ? 0 : 1);
        Grid.SetColumnSpan(Pickers, narrow ? 4 : 1);
        // Two pickers side by side must fit the phone's width (no wrapping row in the header).
        ResolutionPicker.WidthRequest = narrow ? Math.Max(120, Math.Min(170, (Width - 40) / 2)) : 170;
        DisplayPicker.WidthRequest = narrow ? Math.Max(120, Math.Min(300, Width - 40 - ResolutionPicker.WidthRequest - 8)) : 300;

        if (narrow)
        {
            Grid.SetRow(DetailPanel, 3);
            Grid.SetColumn(DetailPanel, 0);
            Grid.SetColumnSpan(DetailPanel, 2);
            DetailPanel.WidthRequest = -1;
            DetailPanel.HeightRequest = 300;
            Grid.SetColumnSpan(VideoArea, 2);
        }
        else
        {
            Grid.SetRow(DetailPanel, 2);
            Grid.SetColumn(DetailPanel, 1);
            Grid.SetColumnSpan(DetailPanel, 1);
            DetailPanel.WidthRequest = 340;
            DetailPanel.HeightRequest = -1;
            Grid.SetColumnSpan(VideoArea, 1);
        }
    }
}
