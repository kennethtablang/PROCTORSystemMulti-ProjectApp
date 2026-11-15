namespace PROCTORSystemMulti_ProjectApp
{
    public partial class AppShell : Shell
    {
        private bool _lockShown;

        public AppShell()
        {
            InitializeComponent();
        }

        // Teacher secure mode: ask for the PIN before anything else is shown.
        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (_lockShown || !ClassInsight.TeacherPin.IsSet) return;
            _lockShown = true;
            await Navigation.PushModalAsync(new LockPage(), animated: false);
        }
    }
}
