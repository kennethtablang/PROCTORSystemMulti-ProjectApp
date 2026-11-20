using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// The lesson's activity log: programs and websites each student used, blocked sites, flash drives,
/// joins and leaves, and the exam — newest first, for everyone or one student, exportable as CSV.
/// Kept on this device for the session (the server stores nothing).
/// </summary>
public sealed class ActivityPage : ContentPage
{
    private const string Everyone = "Everyone";
    private readonly DashboardViewModel _vm;
    private readonly Picker _who;
    private readonly Picker _kind;
    private readonly CollectionView _list;
    private readonly Label _count = new() { FontSize = 12, TextColor = Color.FromArgb("#9AA0A6"), VerticalOptions = LayoutOptions.Center };

    private static readonly string[] Kinds = ["Everything", "Programs", "Websites", "Blocked sites / programs", "Flash drives", "Joined / left"];

    public ActivityPage(DashboardViewModel vm)
    {
        _vm = vm;
        BackgroundColor = Color.FromArgb("#B3000000");

        _who = new Picker { WidthRequest = 200, ItemsSource = Names(), SelectedIndex = 0 };
        _who.SelectedIndexChanged += (_, _) => Refresh();
        _kind = new Picker { WidthRequest = 210, ItemsSource = Kinds, SelectedIndex = 0 };
        _kind.SelectedIndexChanged += (_, _) => Refresh();

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            EmptyView = new Label { Text = "Nothing yet. Activity appears here as students work.", TextColor = Color.FromArgb("#9AA0A6"), HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 30) },
            ItemTemplate = new DataTemplate(() =>
            {
                var time = new Label { FontSize = 12, TextColor = Color.FromArgb("#9AA0A6"), WidthRequest = 64, VerticalOptions = LayoutOptions.Start };
                time.SetBinding(Label.TextProperty, nameof(ActivityEntry.TimeText));
                var kind = new Label { FontSize = 12, FontAttributes = FontAttributes.Bold, WidthRequest = 96, VerticalOptions = LayoutOptions.Start };
                kind.SetBinding(Label.TextProperty, nameof(ActivityEntry.Kind));
                kind.SetBinding(Label.TextColorProperty, nameof(ActivityEntry.KindColor));
                var who = new Label { FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation };
                who.SetBinding(Label.TextProperty, nameof(ActivityEntry.Who));
                var detail = new Label { FontSize = 12, TextColor = Color.FromArgb("#D0D3D8"), LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 };
                detail.SetBinding(Label.TextProperty, nameof(ActivityEntry.Detail));
                var grid = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                    ColumnSpacing = 8,
                    Padding = new Thickness(0, 4),
                };
                grid.Add(time, 0, 0);
                grid.Add(kind, 1, 0);
                grid.Add(new VerticalStackLayout { Spacing = 0, Children = { who, detail } }, 2, 0);
                return grid;
            }),
        };

        var export = new Button { Text = "Export CSV", Padding = new Thickness(14, 4), BackgroundColor = Color.FromArgb("#7E57C2"), TextColor = Colors.White };
        export.Clicked += async (_, _) => await ExportAsync();
        var clear = new Button { Text = "Clear", Padding = new Thickness(14, 4), BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#EF9A9A"), BorderColor = Color.FromArgb("#EF9A9A"), BorderWidth = 1 };
        clear.Clicked += async (_, _) =>
        {
            if (!await DisplayAlertAsync("Clear the activity log?", "Everything recorded so far this session is removed from this app.", "Clear", "Cancel")) return;
            _vm.Activity.Clear();
            Refresh();
        };
        var close = new Button { Text = "Close", Padding = new Thickness(16, 4), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var header = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
            Children = { Gap(_who), Gap(_kind), Gap(_count) },
        };
        var footer = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.End,
            Children = { Gap(clear), Gap(export), Gap(close) },
        };

        Content = ModalUi.Page(this, "Activity log",
            "Programs and websites students used, blocked sites, flash drives and who joined, newest first. Kept in this app for this session; export it to keep a copy.",
            820,
            (header, false),
            (_list, true),
            (footer, false));

        _vm.Activity.CollectionChanged += OnActivityChanged;
        Refresh();
    }

    private string? SelectedStudent => _who.SelectedItem as string is { } s && s != Everyone ? s : null;

    // New entries arrive several times a second with a full class: redraw at most once a second.
    private volatile bool _dirty;
    private IDispatcherTimer? _refreshTimer;

    private void OnActivityChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _dirty = true;
        if (_refreshTimer is not null) return;
        _refreshTimer = Dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.Tick += (_, _) => { if (_dirty) { _dirty = false; Refresh(); } };
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        var student = SelectedStudent;
        var kind = _kind.SelectedIndex;
        var items = _vm.Activity.Where(a => (student is null || a.Computer == student) && Matches(a.Kind, kind)).Take(1000).ToList();
        _list.ItemsSource = items;
        _count.Text = $"{items.Count} entr{(items.Count == 1 ? "y" : "ies")}";
    }

    private static bool Matches(string kind, int filter) => filter switch
    {
        1 => kind == "Program",
        2 => kind == "Website",
        3 => kind is "Blocked site" or "Blocked program",
        4 => kind == "Flash drive",
        5 => kind is "Joined" or "Left",
        _ => true,
    };

    private List<string> Names() =>
        [Everyone, .. _vm.Activity.Select(a => a.Computer).Concat(_vm.Students.Select(s => s.MachineName))
            .Where(n => n != "Class").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, NaturalComparer.Instance)];

    private async Task ExportAsync()
    {
        if (_vm.Activity.Count == 0) { await DisplayAlertAsync("Activity log", "There's nothing to export yet.", "OK"); return; }
        var path = _vm.ExportActivityCsv(SelectedStudent);
        await TeacherFiles.ShowSavedAsync(this, "Activity log exported",
            OperatingSystem.IsWindows() ? path : "Saved the activity log (CSV, opens in Excel or Sheets).", [path]);
    }

    private static View Gap(View v) { v.Margin = new Thickness(0, 0, 8, 4); return v; }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Activity.CollectionChanged -= OnActivityChanged;
        _refreshTimer?.Stop();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = Navigation.PopModalAsync();
        return true;
    }
}
