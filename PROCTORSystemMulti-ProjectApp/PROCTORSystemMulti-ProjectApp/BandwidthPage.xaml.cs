using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Classroom-wide bandwidth overview (FR-TEACH-03): every connected student's live down/up rate
/// and session totals, driven by the same shared <see cref="DashboardViewModel"/> as the other tabs.
/// </summary>
public partial class BandwidthPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public BandwidthPage(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Idempotent: connects once whichever tab the teacher lands on first.
        await _vm.AutoConnectAsync();
    }
}
