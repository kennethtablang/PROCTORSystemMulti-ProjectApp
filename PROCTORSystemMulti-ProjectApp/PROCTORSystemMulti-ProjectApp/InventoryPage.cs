using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// PC inventory: what every connected student PC is (maker, model, processor, memory, Windows
/// drive space, Windows version, when it started, agent version), with low disk space and older
/// agents flagged. Exportable as CSV for the lab technician.
/// </summary>
public sealed class InventoryPage : ContentPage
{
    private readonly DashboardViewModel _vm;
    private readonly CollectionView _list;
    private readonly Label _summary = ModalUi.Text("", 12, ModalUi.Muted);
    private IDispatcherTimer? _timer;

    public InventoryPage(DashboardViewModel vm)
    {
        _vm = vm;
        BackgroundColor = ModalUi.Overlay;

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            EmptyView = new Label { Text = "No student PCs are connected.", TextColor = ModalUi.Muted, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 30) },
            ItemTemplate = new DataTemplate(() =>
            {
                Label L(string path, double size, Color color, bool bold = false)
                {
                    var l = new Label { FontSize = size, TextColor = color, LineBreakMode = LineBreakMode.WordWrap, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None };
                    l.SetBinding(Label.TextProperty, path);
                    return l;
                }
                var title = L(nameof(InventoryRow.Title), 13, Colors.White, bold: true);
                var network = L(nameof(InventoryRow.Network), 11, ModalUi.Muted);
                var hardware = L(nameof(InventoryRow.Hardware), 12, ModalUi.Body);
                var disk = L(nameof(InventoryRow.Disk), 12, ModalUi.Body);
                disk.SetBinding(Label.TextColorProperty, nameof(InventoryRow.DiskColor));
                var windows = L(nameof(InventoryRow.Windows), 12, ModalUi.Body);
                var agent = L(nameof(InventoryRow.Agent), 11, ModalUi.Muted);
                agent.SetBinding(Label.TextColorProperty, nameof(InventoryRow.AgentColor));
                return new Border
                {
                    Stroke = Color.FromArgb("#3A3E47"),
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                    Padding = new Thickness(12, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout { Spacing = 2, Children = { title, network, hardware, disk, windows, agent } },
                };
            }),
        };

        var export = ModalUi.Primary("Export CSV");
        export.Clicked += async (_, _) => await ExportAsync();
        var refresh = ModalUi.Secondary("Refresh");
        refresh.Clicked += (_, _) => Refresh();
        var close = ModalUi.Secondary("Close");
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Content = ModalUi.Page("PC inventory",
            "Each student PC reports its hardware, Windows version, free disk space and agent version " +
            "(within 30 seconds of connecting). Low disk space and older agents are shown in amber.",
            900,
            (_summary, false),
            (_list, true),
            (ModalUi.Row(true, refresh, export, close), false));

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
        var newest = _vm.NewestAgentVersion;
        var rows = _vm.Students.OrderBy(s => s.MachineName, NaturalComparer.Instance).Select(t => new InventoryRow(t, newest)).ToList();
        _list.ItemsSource = rows;

        var waiting = rows.Count(r => r.Waiting);
        var lowDisk = rows.Count(r => r.LowDisk);
        var oldAgent = rows.Count(r => r.OldAgent);
        var parts = new List<string> { rows.Count == 1 ? "1 PC" : $"{rows.Count} PCs" };
        if (lowDisk > 0) parts.Add($"{lowDisk} low on disk space");
        if (oldAgent > 0) parts.Add($"{oldAgent} with an older agent");
        if (waiting > 0) parts.Add($"{waiting} still reporting");
        _summary.Text = string.Join(" · ", parts);
    }

    private async Task ExportAsync()
    {
        if (_vm.Students.Count == 0) { await DisplayAlertAsync("PC inventory", "No student PCs are connected.", "OK"); return; }
        var path = _vm.ExportInventoryCsv();
        await TeacherFiles.ShowSavedAsync(this, "Inventory exported",
            OperatingSystem.IsWindows() ? path : "Saved the PC inventory (CSV, opens in Excel or Sheets).", [path]);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
    }

    protected override bool OnBackButtonPressed() => ModalUi.Back(this);
}

/// <summary>One PC in the inventory list, pre-formatted.</summary>
public sealed class InventoryRow
{
    private static readonly Color Amber = Color.FromArgb("#FFB74D");

    public InventoryRow(StudentTile t, Version? newestAgent)
    {
        var i = t.Inventory;
        Title = t.Header;
        Network = string.Join("  ·  ", new[] { t.IpAddress, string.Join(", ", t.MacAddresses) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        Waiting = i is null;
        if (i is null)
        {
            Hardware = "Waiting for this PC's report… (older agents don't send one: update the agent)";
            return;
        }

        var model = string.Join(" ", new[] { i.Manufacturer, i.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var cpu = i.CpuName is { } c ? $"{c} ({i.CpuCores} threads)" : $"{i.CpuCores} threads";
        Hardware = string.Join("  ·  ", new[] { model, cpu, i.MemoryTotalBytes > 0 ? $"{StudentTile.Human(i.MemoryTotalBytes)} RAM" : null }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        if (i.DiskTotalBytes > 0)
        {
            var percent = i.DiskFreeBytes * 100.0 / i.DiskTotalBytes;
            LowDisk = percent < 10 || i.DiskFreeBytes < 5L * 1024 * 1024 * 1024;
            Disk = $"Windows drive: {StudentTile.Human(i.DiskFreeBytes)} free of {StudentTile.Human(i.DiskTotalBytes)} ({percent:0}%)" + (LowDisk ? " — low" : "");
        }

        var started = i.BootedAt is { } b ? $"  ·  started {b.ToLocalTime():ddd d MMM, HH:mm} (up {StudentTile.FormatDuration((int)Math.Max(60, (DateTimeOffset.UtcNow - b).TotalSeconds))})" : "";
        Windows = (i.Windows ?? "Windows") + started;

        var version = ProductVersion.Parse(i.AgentVersion);
        OldAgent = version is not null && newestAgent is not null && version < newestAgent;
        Agent = $"PROCTOR agent {i.AgentVersion ?? "?"}" + (OldAgent ? $" — older than {ProductVersion.Format(newestAgent!)}; it updates itself from the teacher PC" : "");
    }

    public string Title { get; }
    public string Network { get; }
    public string Hardware { get; } = "";
    public string Disk { get; } = "";
    public string Windows { get; } = "";
    public string Agent { get; } = "";
    public bool Waiting { get; }
    public bool LowDisk { get; }
    public bool OldAgent { get; }
    public Color DiskColor => LowDisk ? Amber : ModalUi.Body;
    public Color AgentColor => OldAgent ? Amber : ModalUi.Muted;
}
