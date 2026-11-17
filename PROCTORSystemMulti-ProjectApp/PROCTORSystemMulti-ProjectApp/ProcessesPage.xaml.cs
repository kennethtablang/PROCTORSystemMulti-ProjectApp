using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Classroom-wide process overview (FR-TEACH-03): every connected student with their live top
/// processes, driven by the same shared <see cref="DashboardViewModel"/> as the other tabs.
/// </summary>
public partial class ProcessesPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public ProcessesPage(DashboardViewModel vm)
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
