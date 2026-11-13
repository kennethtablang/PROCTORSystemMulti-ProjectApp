namespace PROCTORSystemMulti_ProjectApp
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "ClassInsight",
            };

            // Replace the default OS title bar with integrated app chrome. The window keeps its
            // native minimize/maximize/close buttons and stays draggable and resizable, but the
            // plain caption strip is gone and the app's own branding sits in its place — the
            // "looks like a real app" chrome the other classroom software has.
            window.TitleBar = new TitleBar
            {
                BackgroundColor = Color.FromArgb("#15171C"),
                ForegroundColor = Colors.White,
                HeightRequest = 44,
                LeadingContent = new Label
                {
                    Text = "ClassInsight",
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#B794F6"),
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(16, 0, 0, 0),
                },
            };

            return window;
        }
    }
}
