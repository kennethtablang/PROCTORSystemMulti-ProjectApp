using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Usage report: per student, time online / active / idle, the programs and websites they spent the
/// most time in, and blocked attempts, since the report began. Exportable as CSV.
/// </summary>
public sealed class UsagePage : ContentPage
{
    private readonly DashboardViewModel _vm;
    private readonly CollectionView _list;
    private readonly Label _since = ModalUi.Text("", 12, ModalUi.Muted);
    private IDispatcherTimer? _timer;

    public UsagePage(DashboardViewModel vm)
    {
        _vm = vm;
        BackgroundColor = ModalUi.Overlay;

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            EmptyView = new Label { Text = "Nothing yet. Usage is counted while students are connected.", TextColor = ModalUi.Muted, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 30) },
            ItemTemplate = new DataTemplate(() =>
            {
                Label L(string path, double size, Color color, bool bold = false)
                {
                    var l = new Label { FontSize = size, TextColor = color, LineBreakMode = LineBreakMode.WordWrap, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None };
                    l.SetBinding(Label.TextProperty, path);
                    return l;
                }
                var blocked = L(nameof(UsageRow.Blocked), 12, Color.FromArgb("#EF9A9A"));
                blocked.SetBinding(IsVisibleProperty, nameof(UsageRow.HasBlocked));
                return new Border
                {
                    Stroke = Color.FromArgb("#3A3E47"),
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                    Padding = new Thickness(12, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children =
                        {
                            L(nameof(UsageRow.Title), 13, Colors.White, bold: true),
                            L(nameof(UsageRow.Totals), 12, ModalUi.Body),
                            L(nameof(UsageRow.Apps), 12, ModalUi.Body),
                            L(nameof(UsageRow.Sites), 12, Color.FromArgb("#B39DDB")),
                            blocked,
                        },
                    },
                };
            }),
        };

        var export = ModalUi.Primary("Export CSV");
        export.Clicked += async (_, _) => await ExportAsync();
        var reset = ModalUi.Danger("Start over");
        reset.Clicked += async (_, _) =>
        {
            if (!await DisplayAlertAsync("Start a new report?", "The time counted so far is cleared (export it first to keep it).", "Start over", "Cancel")) return;
            _vm.ResetUsage();
            Refresh();
        };
        var close = ModalUi.Secondary("Close");
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Content = ModalUi.Page("Usage report",
            "How long each student spent in each program and website, and how long their PC sat idle (no keyboard or mouse for a minute or more). " +
            "Counted in this app while students are connected; export it at the end of the class to keep it.",
            900,
            (_since, false),
            (_list, true),
            (ModalUi.Row(true, reset, export, close), false));

        Refresh();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var rows = _vm.Usage.OrderBy(r => r.Computer, NaturalComparer.Instance).Select(r => new UsageRow(r)).ToList();
        _list.ItemsSource = rows;
        _since.Text = $"Since {_vm.UsageSince:ddd d MMM, HH:mm} · {rows.Count} student PC(s)";
    }

    private async Task ExportAsync()
    {
        if (_vm.Usage.Count == 0) { await DisplayAlertAsync("Usage report", "There's nothing to export yet.", "OK"); return; }
        var path = _vm.ExportUsageCsv();
        await TeacherFiles.ShowSavedAsync(this, "Usage report exported",
            OperatingSystem.IsWindows() ? path : "Saved the usage report (CSV, opens in Excel or Sheets).", [path]);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
    }

    protected override bool OnBackButtonPressed() => ModalUi.Back(this);
}

/// <summary>One student in the usage report, pre-formatted.</summary>
public sealed class UsageRow
{
    public UsageRow(UsageRecord r)
    {
        Title = string.IsNullOrEmpty(r.User) ? r.Computer : $"{r.Computer} · {r.User}";
        var activePct = r.Online > 0 ? r.Active * 100 / r.Online : 0;
        Totals = $"Online {Minutes(r.Online)} · active {Minutes(r.Active)} ({activePct:0}%) · idle {Minutes(r.Idle)}";
        Apps = "Programs: " + Top(r.Apps, "none yet");
        Sites = "Websites: " + Top(r.Sites, "none");
        Blocked = r.Blocked == 1 ? "1 blocked site or program attempt" : $"{r.Blocked} blocked site or program attempts";
        HasBlocked = r.Blocked > 0;
    }

    public string Title { get; }
    public string Totals { get; }
    public string Apps { get; }
    public string Sites { get; }
    public string Blocked { get; }
    public bool HasBlocked { get; }

    private static string Top(Dictionary<string, double> d, string none) => d.Count == 0
        ? none
        : string.Join(" · ", d.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {Minutes(kv.Value)}"));

    private static string Minutes(double seconds) => seconds < 60 ? $"{seconds:0} s" : seconds < 3600 ? $"{seconds / 60:0} min" : $"{(int)(seconds / 3600)} h {(int)(seconds / 60 % 60)} min";
}
