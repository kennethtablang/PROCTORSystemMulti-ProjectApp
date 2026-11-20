using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Teacher alerts: what raises one (blocked sites and programs, flash drives, watched words, idle
/// students, leaving an exam, sound) and every alert this session, newest first. Opening the page
/// clears the banner and the red outlines.
/// </summary>
public sealed class AlertsPage : ContentPage
{
    private static readonly int[] IdleChoices = [2, 5, 10, 15, 30, 60];
    private readonly DashboardViewModel _vm;
    private readonly CollectionView _list;
    private readonly Label _count = ModalUi.Text("", 12, ModalUi.Muted);
    private readonly Entry _words;

    public AlertsPage(DashboardViewModel vm)
    {
        _vm = vm;
        BackgroundColor = ModalUi.Overlay;
        var s = vm.AlertSettings;

        void Set(Func<AlertSettings, AlertSettings> change) => _vm.AlertSettings = change(_vm.AlertSettings);

        var idleMinutes = new Picker
        {
            ItemsSource = IdleChoices.Select(m => $"{m} min").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(IdleChoices, s.IdleMinutes)),
            WidthRequest = 90,
            VerticalOptions = LayoutOptions.Center,
        };
        if (Array.IndexOf(IdleChoices, s.IdleMinutes) < 0) idleMinutes.SelectedIndex = 1;
        idleMinutes.SelectedIndexChanged += (_, _) =>
        {
            if (idleMinutes.SelectedIndex >= 0) Set(a => a with { IdleMinutes = IdleChoices[idleMinutes.SelectedIndex] });
        };

        var when = ModalUi.Row(false,
            ModalUi.Toggle("Alerts on", s.Enabled, v => Set(a => a with { Enabled = v })),
            ModalUi.Toggle("Blocked site opened", s.BlockedSites, v => Set(a => a with { BlockedSites = v })),
            ModalUi.Toggle("Blocked program opened", s.BlockedPrograms, v => Set(a => a with { BlockedPrograms = v })),
            ModalUi.Toggle("Flash drive plugged in", s.FlashDrives, v => Set(a => a with { FlashDrives = v })),
            ModalUi.Toggle("Left during an exam", s.LeftDuringExam, v => Set(a => a with { LeftDuringExam = v })),
            new HorizontalStackLayout
            {
                Spacing = 4,
                Children = { ModalUi.Toggle("No keyboard or mouse for", s.Idle, v => Set(a => a with { Idle = v })), idleMinutes },
            },
            ModalUi.Toggle(OperatingSystem.IsWindows() ? "Play a sound" : "Vibrate", s.Sound, v => Set(a => a with { Sound = v })));

        _words = new Entry
        {
            Text = string.Join(", ", s.Words),
            Placeholder = "Watched words, e.g. chatgpt, game, roblox, facebook",
            MinimumWidthRequest = 260,
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
        };
        _words.Completed += (_, _) => SaveWords();
        _words.Unfocused += (_, _) => SaveWords();
        var wordsRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        wordsRow.Add(_words, 0, 0);
        var saveWords = ModalUi.Secondary("Save words");
        saveWords.Clicked += (_, _) => SaveWords();
        wordsRow.Add(saveWords, 1, 0);

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            EmptyView = new Label { Text = "No alerts yet.", TextColor = ModalUi.Muted, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 30) },
            ItemTemplate = new DataTemplate(() =>
            {
                var time = new Label { FontSize = 12, TextColor = ModalUi.Muted, WidthRequest = 64 };
                time.SetBinding(Label.TextProperty, nameof(AlertEntry.TimeText));
                var kind = new Label { FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#EF5350"), WidthRequest = 110 };
                kind.SetBinding(Label.TextProperty, nameof(AlertEntry.Kind));
                var who = new Label { FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation };
                who.SetBinding(Label.TextProperty, nameof(AlertEntry.Who));
                var detail = new Label { FontSize = 12, TextColor = ModalUi.Body, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 };
                detail.SetBinding(Label.TextProperty, nameof(AlertEntry.Detail));
                var grid = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                    ColumnSpacing = 8,
                    Padding = new Thickness(0, 4),
                };
                grid.Add(time, 0, 0);
                grid.Add(kind, 1, 0);
                grid.Add(new VerticalStackLayout { Children = { who, detail } }, 2, 0);
                return grid;
            }),
        };

        var clear = ModalUi.Danger("Clear list");
        clear.Clicked += (_, _) => { _vm.Alerts.Clear(); _vm.DismissAlerts(); Refresh(); };
        var close = ModalUi.Secondary("Close");
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Content = ModalUi.Page("Alerts",
            "Get a pop-up and a red outline on the student's screen when something needs your attention. " +
            "Watched words are checked in window titles and websites (separate them with commas).",
            860,
            (ModalUi.Text("Alert me when", 13, ModalUi.Accent), false),
            (when, false),
            (wordsRow, false),
            (ModalUi.Row(false, ModalUi.Text("This session", 13, ModalUi.Accent), _count), false),
            (_list, true),
            (ModalUi.Row(true, clear, close), false));

        _vm.Alerts.CollectionChanged += OnAlertsChanged;
        Refresh();
    }

    private void SaveWords() => _vm.AlertSettings = _vm.AlertSettings with { Words = AlertSettings.ParseWords(_words.Text) };

    private void OnAlertsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.Dispatch(() => { Refresh(); _vm.DismissAlerts(); });

    private void Refresh()
    {
        var items = _vm.Alerts.ToList();
        _list.ItemsSource = items;
        _count.Text = items.Count == 1 ? "1 alert" : $"{items.Count} alerts";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.DismissAlerts();   // seen
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SaveWords();
        _vm.Alerts.CollectionChanged -= OnAlertsChanged;
    }

    protected override bool OnBackButtonPressed() => ModalUi.Back(this);
}
