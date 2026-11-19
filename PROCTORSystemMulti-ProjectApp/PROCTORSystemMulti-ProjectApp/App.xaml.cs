namespace PROCTORSystemMulti_ProjectApp
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // Light / dark mode: the teacher's choice (Settings or the toolbar), else the device's.
            PROCTOR.Appearance.Apply();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "PROCTOR",
            };

            // Replace the default OS title bar with integrated app chrome. The window keeps its
            // native minimize/maximize/close buttons and stays draggable and resizable, but the
            // plain caption strip is gone and the app's own branding sits in its place — the
            // "looks like a real app" chrome the other classroom software has.
            window.TitleBar = new TitleBar
            {
                BackgroundColor = Color.FromArgb("#15171C"),
                ForegroundColor = Colors.White,
                HeightRequest = 36,
                LeadingContent = new HorizontalStackLayout
                {
                    Spacing = 8,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(12, 0, 0, 0),
                    Children =
                    {
                        // The PROCTOR logo (branding\proctor-logo.svg).
                        new Image { Source = "proctor_logo.png", WidthRequest = 18, HeightRequest = 18, VerticalOptions = LayoutOptions.Center },
                        new Label
                        {
                            Text = "PROCTOR",
                            FontSize = 13,
                            FontAttributes = FontAttributes.Bold,
                            TextColor = Color.FromArgb("#B794F6"),
                            VerticalOptions = LayoutOptions.Center,
                        },
                    },
                },
                // "More information" on every page: version, address for student PCs, update check, links.
                TrailingContent = MoreInfoButton(),
            };

            // Offer GitHub updates while the app is open (first check shortly after start).
            PROCTOR.UpdateNotifier.Start();
            // Android installs a downloaded update by itself once the app is left in the background.
            window.Stopped += (_, _) => PROCTOR.UpdateNotifier.OnAppStopped();
            window.Resumed += (_, _) => PROCTOR.UpdateNotifier.OnAppResumed();

            return window;
        }

        private static Button MoreInfoButton()
        {
            var button = new PROCTOR.IconButton
            {
                Glyph = PROCTOR.Icons.Info,
                ToolTip = "More information: version, server key, updates",
                IconColor = Color.FromArgb("#B794F6"),
                Margin = new Thickness(0, 0, 8, 0),
                BackgroundColor = Colors.Transparent,
                BorderWidth = 0,
                VerticalOptions = LayoutOptions.Center,
            };
            button.Clicked += async (_, _) =>
            {
                var navigation = Shell.Current?.Navigation;
                if (navigation is null || navigation.ModalStack.LastOrDefault() is LockPage or AboutPage or UpdatePage) return;
                await navigation.PushModalAsync(new AboutPage());
            };
            return button;
        }
    }
}
