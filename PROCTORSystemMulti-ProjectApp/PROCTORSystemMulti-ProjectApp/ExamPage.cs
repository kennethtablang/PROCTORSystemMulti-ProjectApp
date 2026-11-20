using System.Text.Json;
using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Start an exam: one switch for test conditions — which websites, flash drives, sound, programs —
/// and an "Exam in progress" bar on every student screen. The choices are remembered for next time;
/// ending the exam puts the limits from before it back.
/// </summary>
public sealed class ExamPage : ContentPage
{
    private const string PrefSettings = "proctor.exam.settings";
    private static readonly Color Muted = Color.FromArgb("#9AA0A6");
    private static readonly string[] WebChoices =
    [
        "Keep the current website rules",
        "Only these websites (whitelist)",
        "No internet at all",
    ];

    private readonly DashboardViewModel _vm;
    private readonly Entry _title;
    private readonly Picker _web;
    private readonly Editor _sites;
    private readonly Switch _usb;
    private readonly Switch _mute;
    private readonly Editor _apps;
    private readonly Editor _allowed;
    private readonly Label _error = new() { FontSize = 12, TextColor = Color.FromArgb("#EF9A9A"), IsVisible = false, LineBreakMode = LineBreakMode.WordWrap };

    public ExamPage(DashboardViewModel vm)
    {
        _vm = vm;
        BackgroundColor = Color.FromArgb("#B3000000");
        var saved = Load();

        _title = new Entry { Text = saved.Title, Placeholder = "e.g. Quiz 3 — Networking", MaxLength = ClassroomLimits.MaxLockTitleLength };
        _web = new Picker { ItemsSource = WebChoices, SelectedIndex = (int)saved.Web };
        _sites = new Editor
        {
            Text = string.Join(Environment.NewLine, saved.Sites), Placeholder = "One site per line, e.g.\nclassroom.google.com\nforms.office.com",
            HeightRequest = 90, AutoSize = EditorAutoSizeOption.Disabled, IsVisible = saved.Web == ExamWeb.OnlyListed,
        };
        _web.SelectedIndexChanged += (_, _) => _sites.IsVisible = _web.SelectedIndex == (int)ExamWeb.OnlyListed;
        _usb = new Switch { IsToggled = saved.BlockUsb, VerticalOptions = LayoutOptions.Center };
        _mute = new Switch { IsToggled = saved.Mute, VerticalOptions = LayoutOptions.Center };
        _apps = new Editor
        {
            Text = string.Join(Environment.NewLine, saved.BlockedApps), Placeholder = "Optional, one per line, e.g.\ndiscord\nsteam",
            HeightRequest = 70, AutoSize = EditorAutoSizeOption.Disabled,
        };

        _allowed = new Editor
        {
            Text = string.Join(Environment.NewLine, saved.AllowedApps), Placeholder = "Optional: if you list programs, only these can be used, e.g.\nWord\nChrome",
            HeightRequest = 70, AutoSize = EditorAutoSizeOption.Disabled,
        };

        var start = new Button { Text = "Start exam", Padding = new Thickness(18, 4), BackgroundColor = Color.FromArgb("#F2C230"), TextColor = Color.FromArgb("#1F1A00") };
        start.Clicked += async (_, _) => await StartAsync();
        var cancel = new Button { Text = "Cancel", Padding = new Thickness(14, 4), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White };
        cancel.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var body = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "Start an exam", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#F2C230") },
                new Label
                {
                    Text = "Applies to the whole class. Students see an \"Exam in progress\" bar at the bottom of their screen. " +
                           "When you end the exam, the limits from before it come back.",
                    FontSize = 12, TextColor = Color.FromArgb("#D0D3D8"), LineBreakMode = LineBreakMode.WordWrap,
                },
                Caption("Exam name (shown to students)"), _title,
                Caption("Websites"), _web, _sites,
                SwitchRow("Block flash drives", "No copying from or to USB drives during the exam.", _usb),
                SwitchRow("Mute sound", null, _mute),
                Caption("Also block these programs"), _apps,
                Caption("Only these programs (whitelist)"), _allowed,
                _error,
                new FlexLayout
                {
                    Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                    JustifyContent = Microsoft.Maui.Layouts.FlexJustify.End,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { Gap(cancel), Gap(start) },
                },
            },
        };

        Content = new Grid
        {
            Children =
            {
                new Border
                {
                    BackgroundColor = Color.FromArgb("#1E2027"),
                    Stroke = Color.FromArgb("#F2C230"),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                    Padding = new Thickness(20, 16),
                    MaximumWidthRequest = 560,
                    MaximumHeightRequest = 760,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(16),
                    Content = new ScrollView { Content = body },
                },
            },
        };
    }

    private async Task StartAsync()
    {
        var web = (ExamWeb)Math.Max(0, _web.SelectedIndex);
        var sites = Lines(_sites.Text);
        if (web == ExamWeb.OnlyListed)
        {
            var bad = sites.Where(s => ClassroomLimits.CleanHost(s) is null).ToArray();
            if (sites.Length == 0) { ShowError("List the websites students may use, or choose \"No internet at all\"."); return; }
            if (bad.Length > 0) { ShowError($"Not a website: {string.Join(", ", bad.Take(5))}"); return; }
        }
        var apps = Lines(_apps.Text);
        var badApps = apps.Where(a => ClassroomLimits.CleanAppName(a) is null).ToArray();
        if (badApps.Length > 0) { ShowError($"Can't block: {string.Join(", ", badApps.Take(5))} (not a program name, or protected)."); return; }
        var allowed = Lines(_allowed.Text);
        var badAllowed = allowed.Where(a => ClassroomLimits.CleanAllowedApp(a) is null).ToArray();
        if (badAllowed.Length > 0) { ShowError($"Not a program name: {string.Join(", ", badAllowed.Take(5))}."); return; }
        if (!_vm.IsConnected) { ShowError("Not connected to the classroom server."); return; }

        var exam = new ExamSettings
        {
            Title = _title.Text,
            Web = web,
            Sites = sites,
            BlockUsb = _usb.IsToggled,
            Mute = _mute.IsToggled,
            BlockedApps = apps,
            AllowedApps = allowed,
        };
        Save(exam);
        var error = await _vm.StartExamAsync(exam);
        if (error is not null) { ShowError(error); return; }
        await Navigation.PopModalAsync();
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    /// <summary>The exam settings used last time (the schedule offers them for a scheduled exam).</summary>
    public static ExamSettings Saved => Load();

    private static ExamSettings Load()
    {
        try
        {
            var json = Preferences.Default.Get(PrefSettings, "");
            return string.IsNullOrEmpty(json) ? new ExamSettings() : JsonSerializer.Deserialize<ExamSettings>(json) ?? new ExamSettings();
        }
        catch { return new ExamSettings(); }
    }

    private static void Save(ExamSettings exam)
    {
        try { Preferences.Default.Set(PrefSettings, JsonSerializer.Serialize(exam)); } catch { }
    }

    private static string[] Lines(string? text) =>
        (text ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Label Caption(string text) => new()
    {
        Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Muted, Margin = new Thickness(0, 6, 0, 0),
    };

    private static View SwitchRow(string title, string? detail, Switch control)
    {
        var text = new VerticalStackLayout { Spacing = 2, Children = { new Label { Text = title, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Colors.White } } };
        if (detail is not null) text.Children.Add(new Label { Text = detail, FontSize = 11, TextColor = Muted, LineBreakMode = LineBreakMode.WordWrap });
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Margin = new Thickness(0, 6, 0, 0) };
        grid.Add(text, 0, 0);
        grid.Add(control, 1, 0);
        return grid;
    }

    private static View Gap(View v) { v.Margin = new Thickness(8, 4, 0, 0); return v; }

    protected override bool OnBackButtonPressed()
    {
        _ = Navigation.PopModalAsync();
        return true;
    }
}
