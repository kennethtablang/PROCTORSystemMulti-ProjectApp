using PROCTOR.Shared;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// "More information" in the teacher app: what PROCTOR is, this version, the address student PCs
/// connect to, a manual update check, and links to the project, release notes and problem reports.
/// </summary>
public sealed class AboutPage : ContentPage
{
    private readonly Label _updateStatus;
    private readonly Button _check;

    public AboutPage()
    {
        BackgroundColor = Color.FromArgb("#B3000000");
        var vm = IPlatformApplication.Current?.Services.GetService<DashboardViewModel>();

        _updateStatus = new Label { FontSize = 12, TextColor = Color.FromArgb("#9AA0A6"), LineBreakMode = LineBreakMode.WordWrap };
        _check = Primary("Check for updates");
        _check.Clicked += async (_, _) => await CheckAsync();

        var close = Secondary("Close");
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var details = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 14,
            RowSpacing = 6,
        };
        void Row(int r, string label, string value)
        {
            details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            details.Add(new Label { Text = label, FontSize = 13, TextColor = Color.FromArgb("#9AA0A6") }, 0, r);
            details.Add(new Label { Text = value, FontSize = 13, TextColor = Colors.White, LineBreakMode = LineBreakMode.CharacterWrap }, 1, r);
        }
        Row(0, "Version", ProductVersion.Text);
        Row(1, "Student PCs connect to", vm?.StudentServerAddress ?? "—");
        Row(2, "This device", $"{DeviceInfo.Name} · {DeviceInfo.Platform} {DeviceInfo.VersionString}");

        var links = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Children =
            {
                Link("Project page", AppUpdater.ProjectUrl),
                Link("Release notes", AppUpdater.ReleasesUrl),
                Link("Report a problem", AppUpdater.IssuesUrl),
            },
        };

        var card = new Border
        {
            BackgroundColor = Color.FromArgb("#1E2027"),
            Stroke = Color.FromArgb("#B794F6"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(22, 18),
            MaximumWidthRequest = 560,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(16),
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label { Text = "PROCTOR", FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#B794F6") },
                    new Label
                    {
                        Text = "Classroom monitoring and control for computer labs: live student screens, lock, messages, " +
                               "web limits, screen sharing and remote help. Student PCs connect to this teacher's PC on the lab network.",
                        FontSize = 13, TextColor = Color.FromArgb("#D0D3D8"), LineBreakMode = LineBreakMode.WordWrap,
                    },
                    details,
                    links,
                    _updateStatus,
                    new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { _check, close } },
                },
            },
        };
        Content = new Grid { Children = { card } };
    }

    private async Task CheckAsync()
    {
        _check.IsEnabled = false;
        _updateStatus.TextColor = Color.FromArgb("#9AA0A6");
        _updateStatus.Text = "Checking GitHub for updates…";
        var release = await AppUpdater.GetLatestAsync(CancellationToken.None);
        _check.IsEnabled = true;
        if (release is null)
        {
            _updateStatus.Text = "Couldn't check for updates (no internet connection, or no release published yet).";
            return;
        }
        if (!release.IsNewer)
        {
            _updateStatus.Text = $"PROCTOR is up to date ({ProductVersion.Text}; latest release {release.VersionText}).";
            return;
        }
        // A newer one: replace this page with the update pop-up.
        await Navigation.PopModalAsync();
        await UpdateNotifier.TryShowAsync(release);
    }

    private static Button Primary(string text) => new()
    {
        Text = text, Padding = new Thickness(16, 6), BackgroundColor = Color.FromArgb("#7E57C2"), TextColor = Colors.White,
    };

    private static Button Secondary(string text) => new()
    {
        Text = text, Padding = new Thickness(16, 6), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White,
    };

    private static View Link(string text, string url)
    {
        var b = new Button
        {
            Text = text, Padding = new Thickness(10, 2), FontSize = 12, Margin = new Thickness(0, 0, 8, 4),
            BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#B794F6"), BorderColor = Color.FromArgb("#B794F6"), BorderWidth = 1,
        };
        b.Clicked += async (_, _) => await Launcher.Default.OpenAsync(url);
        return b;
    }

    protected override bool OnBackButtonPressed()
    {
        _ = Navigation.PopModalAsync();
        return true;
    }
}
