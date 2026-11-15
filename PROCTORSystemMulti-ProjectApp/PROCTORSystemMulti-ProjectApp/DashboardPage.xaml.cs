using System.ComponentModel;
using PROCTORSystemMulti_ProjectApp.ClassInsight;

namespace PROCTORSystemMulti_ProjectApp;

public partial class DashboardPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public DashboardPage(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // Grid density (FR-TEACH-06). GridItemsLayout isn't in the visual tree, so it can't bind
        // to the page's context; drive its Span from the view model instead.
        ApplyColumns();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.Columns)) ApplyColumns();
    }

    private void ApplyColumns()
    {
        if (TileGrid.ItemsLayout is GridItemsLayout layout) layout.Span = _vm.Columns;
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

    // A tile opens the focus view only on an explicit tap. (We deliberately don't use
    // CollectionView selection for this: on WinUI the list auto-selects its first item when the
    // window gains focus, which would pop open a student's full screen with no click.)
    private async void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not StudentTile tile) return;

        if (!_vm.Client.IsConnected)
        {
            await DisplayAlertAsync("Not connected", "Connect to the classroom server first.", "OK");
            return;
        }

        await Navigation.PushAsync(new FocusPage(tile, _vm));
    }
}
