using PROCTORSystemMulti_ProjectApp.ClassInsight;

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

    private readonly StudentTile _tile;
    private readonly DashboardViewModel _vm;
    private int _display;
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

        SizeChanged += (_, _) => ApplyLayout();
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
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Streams.SetFocus(null);
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (DisplayPicker.SelectedIndex < 0 || DisplayPicker.SelectedIndex == _display) return;
        _display = DisplayPicker.SelectedIndex;
        _vm.Streams.SetFocus(_tile.ConnectionId, _display);
    }

    private async void OnStopClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

    /// <summary>Side panel on wide windows; panel stacked under the video on phones (NFR-USE-03).</summary>
    private void ApplyLayout()
    {
        if (Width <= 0) return;
        var narrow = Width < NarrowWidth;
        if (narrow == _narrow) return;
        _narrow = narrow;

        if (narrow)
        {
            Grid.SetRow(DetailPanel, 2);
            Grid.SetColumn(DetailPanel, 0);
            Grid.SetColumnSpan(DetailPanel, 2);
            DetailPanel.WidthRequest = -1;
            DetailPanel.HeightRequest = 300;
            Grid.SetColumnSpan(VideoArea, 2);
        }
        else
        {
            Grid.SetRow(DetailPanel, 1);
            Grid.SetColumn(DetailPanel, 1);
            Grid.SetColumnSpan(DetailPanel, 1);
            DetailPanel.WidthRequest = 340;
            DetailPanel.HeightRequest = -1;
            Grid.SetColumnSpan(VideoArea, 1);
        }
    }
}
