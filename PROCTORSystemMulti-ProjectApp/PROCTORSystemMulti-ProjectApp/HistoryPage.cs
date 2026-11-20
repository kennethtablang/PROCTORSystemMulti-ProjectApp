using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Screen history: when to record (off / during exams / always), how often and for how long, and a
/// player to scrub through one student's pictures for a day.
/// </summary>
public sealed class HistoryPage : ContentPage
{
    private static readonly string[] Modes = ["Off", "During exams only", "Always while connected"];
    private readonly DashboardViewModel _vm;
    private readonly Picker _day;
    private readonly Picker _computer;
    private readonly Image _image = new() { Aspect = Aspect.AspectFit };
    private readonly Label _empty = new() { TextColor = ModalUi.Muted, FontSize = 13, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.Center };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 1, MinimumTrackColor = ModalUi.Accent };
    private readonly Label _time = ModalUi.Text("", 13, Colors.White);
    private readonly IconButton _play;
    private readonly Label _status = ModalUi.Text("", 12, ModalUi.Muted);
    private IReadOnlyList<HistoryDay> _days = [];
    private IReadOnlyList<HistoryShot> _shots = [];
    private int _index = -1;
    private IDispatcherTimer? _player;
    private readonly string? _preferComputer;
    private bool _quiet;   // filling a picker in code: don't react to its change events

    public HistoryPage(DashboardViewModel vm, string? computer = null)
    {
        _vm = vm;
        _preferComputer = computer is null ? null : ScreenHistory.Safe(computer);
        BackgroundColor = ModalUi.Overlay;
        var s = vm.HistorySettings;

        // ---- settings
        var mode = new Picker { ItemsSource = Modes, SelectedIndex = (int)s.Mode, WidthRequest = 210 };
        mode.SelectedIndexChanged += (_, _) => { if (mode.SelectedIndex >= 0) Apply(h => h with { Mode = (HistoryMode)mode.SelectedIndex }); };
        var every = new Picker { ItemsSource = HistorySettings.Intervals.Select(i => $"every {i} s").ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(HistorySettings.Intervals, s.IntervalSeconds)), WidthRequest = 120 };
        every.SelectedIndexChanged += (_, _) => { if (every.SelectedIndex >= 0) Apply(h => h with { IntervalSeconds = HistorySettings.Intervals[every.SelectedIndex] }); };
        var keep = new Picker { ItemsSource = HistorySettings.KeepChoices.Select(d => d == 1 ? "keep 1 day" : $"keep {d} days").ToList(), WidthRequest = 130 };
        keep.SelectedIndex = Math.Max(0, Array.IndexOf(HistorySettings.KeepChoices, s.KeepDays));
        if (Array.IndexOf(HistorySettings.KeepChoices, s.KeepDays) < 0) keep.SelectedIndex = 2;
        keep.SelectedIndexChanged += (_, _) => { if (keep.SelectedIndex >= 0) Apply(h => h with { KeepDays = HistorySettings.KeepChoices[keep.SelectedIndex] }); };
        var settings = ModalUi.Row(false, ModalUi.Text("Record", 13, ModalUi.Accent), mode, every, keep, _status);

        // ---- browse
        _day = new Picker { Title = "Day", WidthRequest = 240 };
        _day.SelectedIndexChanged += (_, _) => { if (!_quiet) LoadComputers(); };
        _computer = new Picker { Title = "Computer", WidthRequest = 170 };
        _computer.SelectedIndexChanged += (_, _) => { if (!_quiet) LoadShots(keepTime: true); };
        var reload = ModalUi.Secondary("Refresh");
        reload.Clicked += (_, _) => LoadDays();
        var browse = ModalUi.Row(false, _day, _computer, reload);

        var screen = new Border
        {
            BackgroundColor = Colors.Black,
            Stroke = Color.FromArgb("#3A3E47"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            MinimumHeightRequest = 160,
            Content = new Grid { Children = { _image, _empty } },
        };

        _slider.ValueChanged += (_, e) =>
        {
            var i = (int)Math.Round(e.NewValue);
            if (i != _index) Show(i);
        };

        var prev = Icon(Icons.Previous, "Previous picture");
        prev.Clicked += (_, _) => { Stop(); Show(_index - 1); };
        _play = Icon(Icons.Play, "Play");
        _play.Clicked += (_, _) => TogglePlay();
        var next = Icon(Icons.Next, "Next picture");
        next.Clicked += (_, _) => { Stop(); Show(_index + 1); };
        var save = ModalUi.Secondary("Save this picture");
        save.Clicked += async (_, _) => await SaveAsync();
        var delete = ModalUi.Danger("Delete this day");
        delete.Clicked += async (_, _) => await DeleteDayAsync();
        var close = ModalUi.Secondary("Close");
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var player = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8 };
        player.Add(new HorizontalStackLayout { Spacing = 4, Children = { prev, _play, next } }, 0, 0);
        player.Add(_slider, 1, 0);

        Content = ModalUi.Page(this, "Screen history",
            "A small picture of every student's screen is saved on this device every few seconds, only when the screen changed. " +
            "Pick a day and a computer, then drag the bar or press play. Old days are deleted automatically.",
            1100,
            (settings, false),
            (browse, false),
            (screen, true),
            (player, false),
            (ModalUi.Row(false, _time), false),
            (ModalUi.Row(true, save, delete, close), false));

        UpdateStatus();
        LoadDays();
    }

    private static IconButton Icon(string glyph, string tip) => new() { Glyph = glyph, ToolTip = tip };

    private void Apply(Func<HistorySettings, HistorySettings> change)
    {
        _vm.HistorySettings = change(_vm.HistorySettings);
        UpdateStatus();
    }

    private void UpdateStatus() => _status.Text = _vm.IsRecordingHistory
        ? "● recording now"
        : _vm.HistorySettings.Mode == HistoryMode.DuringExams ? "starts with the next exam" : "";

    // ---- browsing ----

    private void LoadDays()
    {
        var selected = _day.SelectedIndex >= 0 && _day.SelectedIndex < _days.Count ? _days[_day.SelectedIndex].Folder : null;
        _days = ScreenHistory.Days();
        var at = selected is null ? -1 : _days.ToList().FindIndex(d => d.Folder == selected);
        _quiet = true;
        _day.ItemsSource = _days.Select(d => d.Title).ToList();
        _day.SelectedIndex = _days.Count == 0 ? -1 : Math.Max(0, at);
        _quiet = false;
        LoadComputers();
    }

    private void LoadComputers()
    {
        var previous = _computer.SelectedItem as string ?? _preferComputer;
        var computers = _day.SelectedIndex >= 0 && _day.SelectedIndex < _days.Count ? ScreenHistory.Computers(_days[_day.SelectedIndex]) : [];
        var at = previous is null ? -1 : computers.ToList().FindIndex(c => c.Equals(previous, StringComparison.OrdinalIgnoreCase));
        _quiet = true;
        _computer.ItemsSource = computers.ToList();
        _computer.SelectedIndex = computers.Count == 0 ? -1 : Math.Max(0, at);
        _quiet = false;
        LoadShots(keepTime: true);
    }

    private void LoadShots(bool keepTime)
    {
        Stop();
        var at = _index >= 0 && _index < _shots.Count ? _shots[_index].At.TimeOfDay : (TimeSpan?)null;
        _shots = _day.SelectedIndex >= 0 && _day.SelectedIndex < _days.Count && _computer.SelectedItem is string c
            ? ScreenHistory.Shots(_days[_day.SelectedIndex], c)
            : [];

        _slider.Maximum = Math.Max(1, _shots.Count - 1);
        _slider.IsEnabled = _shots.Count > 1;
        _index = -1;
        if (_shots.Count == 0)
        {
            _image.Source = null;
            _empty.Text = _days.Count == 0
                ? "No screen history yet.\nChoose when to record above; pictures appear here while students are connected."
                : "No pictures for this computer on this day.";
            _empty.IsVisible = true;
            _time.Text = "";
            return;
        }
        // Same moment on another computer (comparing what students did at one time), else the latest.
        var start = _shots.Count - 1;
        if (keepTime && at is { } t)
        {
            var near = _shots.Select((s, i) => (Gap: Math.Abs((s.At.TimeOfDay - t).TotalSeconds), i)).MinBy(x => x.Gap);
            start = near.i;
        }
        Show(start);
    }

    private void Show(int index)
    {
        if (_shots.Count == 0) return;
        index = Math.Clamp(index, 0, _shots.Count - 1);
        _index = index;
        var shot = _shots[index];
        try
        {
            var bytes = File.ReadAllBytes(shot.File);
            _image.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
            _empty.IsVisible = false;
        }
        catch
        {
            _image.Source = null;
            _empty.Text = "This picture couldn't be opened.";
            _empty.IsVisible = true;
        }
        if (Math.Abs(_slider.Value - index) > 0.01) _slider.Value = index;
        _time.Text = $"{shot.At:HH:mm:ss}  ·  picture {index + 1} of {_shots.Count}  ·  {_computer.SelectedItem}";
    }

    // ---- playback ----

    private void TogglePlay()
    {
        if (_player is { IsRunning: true }) { Stop(); return; }
        if (_shots.Count < 2) return;
        if (_index >= _shots.Count - 1) Show(0);
        _player ??= CreatePlayer();
        _player.Start();
        _play.Glyph = Icons.Pause;
        _play.ToolTip = "Pause";
    }

    private IDispatcherTimer CreatePlayer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.Tick += (_, _) =>
        {
            if (_index >= _shots.Count - 1) { Stop(); return; }
            Show(_index + 1);
        };
        return timer;
    }

    private void Stop()
    {
        _player?.Stop();
        _play.Glyph = Icons.Play;
        _play.ToolTip = "Play";
    }

    // ---- actions ----

    private async Task SaveAsync()
    {
        if (_index < 0 || _index >= _shots.Count) { await DisplayAlertAsync("Screen history", "Pick a picture first.", "OK"); return; }
        var shot = _shots[_index];
        var name = $"{_computer.SelectedItem}_{shot.At:yyyy-MM-dd_HHmmss}.jpg";
        var path = TeacherFiles.Save("Screenshots", name, await File.ReadAllBytesAsync(shot.File));
        await TeacherFiles.ShowSavedAsync(this, "Picture saved", OperatingSystem.IsWindows() ? path : "Saved the picture.", [path]);
    }

    private async Task DeleteDayAsync()
    {
        if (_day.SelectedIndex < 0 || _day.SelectedIndex >= _days.Count) return;
        var day = _days[_day.SelectedIndex];
        if (!await DisplayAlertAsync("Delete this day?", $"Every picture from {day.Title} is deleted from this device.", "Delete", "Cancel")) return;
        Stop();
        _image.Source = null;
        await Task.Run(() => ScreenHistory.Delete(day));
        _quiet = true;
        _day.SelectedIndex = -1;
        _quiet = false;
        LoadDays();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _player?.Stop();
    }

    protected override bool OnBackButtonPressed() => ModalUi.Back(this);
}
