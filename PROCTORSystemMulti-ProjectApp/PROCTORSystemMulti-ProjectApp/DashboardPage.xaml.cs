using PROCTORSystemMulti_ProjectApp.ClassInsight;

namespace PROCTORSystemMulti_ProjectApp;

public partial class DashboardPage : ContentPage
{
    private readonly DashboardViewModel _vm = new();

    public DashboardPage()
    {
        InitializeComponent();
        BindingContext = _vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // "Just open the app" — try to connect with no code as soon as the grid shows.
        await _vm.AutoConnectAsync();
    }

    private async void OnStudentSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not StudentTile tile) return;

        // Clear selection so the same tile can be reopened later.
        if (sender is CollectionView cv) cv.SelectedItem = null;

        if (!_vm.Client.IsConnected)
        {
            await DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }

        await Navigation.PushAsync(new FocusPage(tile, _vm.Client));
    }
}
