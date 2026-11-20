using PROCTOR.Shared;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// "Update available" pop-up: Update now (download, install, reopen) / Later / More information
/// (the release notes on GitHub). Shown on top of whatever page the teacher is using.
/// </summary>
public sealed class UpdatePage : ContentPage
{
    private readonly AppRelease _release;
    private readonly Button _update;
    private readonly Button _later;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private CancellationTokenSource? _cts;
    private string? _downloaded;   // verified package, kept for "Install" after a permission prompt

    public UpdatePage(AppRelease release)
    {
        _release = release;
        BackgroundColor = Color.FromArgb("#B3000000");   // dim the app behind the card

        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were published with this version." : release.Notes.Trim();
        if (notes.Length > 1500) notes = notes[..1500] + "…";

        _update = new Button
        {
            Text = AppUpdater.CanInstall && release.SetupUrl is not null ? "Update now" : "Get the update",
            BackgroundColor = Color.FromArgb("#7E57C2"),
            TextColor = Colors.White,
            Padding = new Thickness(16, 6),
        };
        _update.Clicked += async (_, _) => await UpdateAsync();

        _later = new Button { Text = "Later", Padding = new Thickness(16, 6), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White };
        _later.Clicked += async (_, _) =>
        {
            _cts?.Cancel();
            AppUpdater.Snooze(_release);
            await CloseAsync();
        };

        var more = new Button { Text = "More information", Padding = new Thickness(16, 6), BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#B794F6"), BorderColor = Color.FromArgb("#B794F6"), BorderWidth = 1 };
        more.Clicked += async (_, _) => await Launcher.Default.OpenAsync(_release.PageUrl);

        _progress = new ProgressBar { IsVisible = false, ProgressColor = Color.FromArgb("#B794F6") };
        _status = new Label { FontSize = 12, TextColor = Color.FromArgb("#9AA0A6"), IsVisible = false, LineBreakMode = LineBreakMode.WordWrap };

        var card = new Border
        {
            BackgroundColor = Color.FromArgb("#1E2027"),
            Stroke = Color.FromArgb("#B794F6"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(22, 18),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(16),
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label { Text = "Update available", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                    new Label
                    {
                        Text = $"PROCTOR {release.VersionText} is ready. You have {ProductVersion.Text}.",
                        FontSize = 14, TextColor = Colors.White,
                    },
                    new Label
                    {
                        Text = OperatingSystem.IsWindows()
                            ? "Updating also updates the classroom service on this PC; student PCs update themselves afterwards. " +
                              "The app closes for about a minute and reopens."
                            : "The new version installs over this app and keeps your saved classrooms and settings. " +
                              "Android asks you to confirm the update.",
                        FontSize = 12, TextColor = Color.FromArgb("#9AA0A6"), LineBreakMode = LineBreakMode.WordWrap,
                    },
                    new Label { Text = release.Title, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#B794F6"), Margin = new Thickness(0, 6, 0, 0) },
                    new ScrollView
                    {
                        MaximumHeightRequest = 220,
                        Content = new Label { Text = notes, FontSize = 13, TextColor = Color.FromArgb("#D0D3D8"), LineBreakMode = LineBreakMode.WordWrap },
                    },
                    _progress,
                    _status,
                    new FlexLayout
                    {
                        Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                        JustifyContent = Microsoft.Maui.Layouts.FlexJustify.End,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children =
                        {
                            WithMargin(more), WithMargin(_later), WithMargin(_update),
                        },
                    },
                },
            },
        };
        Content = new Grid { Children = { card } };
        ModalUi.FitCard(this, 560);   // never wider or taller than the window
    }

    private static View WithMargin(View v) { v.Margin = new Thickness(8, 4, 0, 0); return v; }

    private async Task UpdateAsync()
    {
        if (!AppUpdater.CanInstall || _release.SetupUrl is null)
        {
            await Launcher.Default.OpenAsync(_release.PageUrl);
            return;
        }

        _update.IsEnabled = false;
        _later.Text = "Cancel";
        _progress.IsVisible = true;
        _status.IsVisible = true;
        _status.TextColor = Color.FromArgb("#9AA0A6");
        _cts = new CancellationTokenSource();
        try
        {
            if (_downloaded is null || !File.Exists(_downloaded))
            {
                _status.Text = "Downloading the update…";
                var progress = new Progress<double>(p =>
                {
                    _progress.Progress = p;
                    _status.Text = $"Downloading the update… {p:P0}";
                });
                _downloaded = await Task.Run(() => AppUpdater.DownloadSetupAsync(_release, progress, _cts.Token));
            }

            if (OperatingSystem.IsWindows())
            {
                _status.Text = "Installing. Windows will ask for administrator approval, then PROCTOR reopens by itself.";
                await Task.Delay(1200);
            }
            else
            {
                _status.Text = "Android will ask you to confirm: tap Update.";
            }

            // Android may first need permission to install updates: show what to do, then retry.
            if (await AppUpdater.InstallAsync(_downloaded) is { } todo)
            {
                _status.Text = todo;
                _status.TextColor = Color.FromArgb("#FFD54F");
                _update.Text = "Install";
                _update.IsEnabled = true;
                _later.Text = "Later";
                _progress.IsVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
            // "Cancel" closes the pop-up.
        }
        catch (Exception ex)
        {
            _status.Text = $"The update couldn't be downloaded: {ex.Message} Use More information to download it yourself.";
            _status.TextColor = Color.FromArgb("#EF9A9A");
            _update.IsEnabled = true;
            _update.Text = "Try again";
            _later.Text = "Later";
            _progress.IsVisible = false;
        }
    }

    private async Task CloseAsync()
    {
        if (Navigation.ModalStack.Contains(this)) await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        AppUpdater.Snooze(_release);
        _ = CloseAsync();
        return true;
    }
}
