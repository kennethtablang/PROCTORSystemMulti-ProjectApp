using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Website whitelist / blacklist for the whole class, or for one student (who then follows their
/// own list instead of the class's). The allow list and the block list are both kept on this PC,
/// so switching between them never loses a list. Website limits stay on the student PCs until a
/// teacher changes them, even when no teacher app is connected.
/// </summary>
public sealed class WebsitesPage : ContentPage
{
    private const string PrefAllow = "proctor.web.allow";
    private const string PrefBlock = "proctor.web.block";

    private readonly DashboardViewModel _vm;
    private readonly StudentTile? _student;
    private readonly Picker _mode;
    private readonly Editor _allow;
    private readonly Editor _block;
    private readonly Label _error = new() { TextColor = Color.FromArgb("#EF9A9A"), FontSize = 12, IsVisible = false, LineBreakMode = LineBreakMode.WordWrap };

    // Picker rows. A student's page has "same as the class" first.
    private const string SameAsClass = "Same as the class";
    private const string NoLimits = "No website limits";
    private const string UseBlock = "Block the sites on the block list (blacklist)";
    private const string UseAllow = "Allow only the sites on the allow list (whitelist)";

    public WebsitesPage(DashboardViewModel vm, StudentTile? student = null)
    {
        _vm = vm;
        _student = student;
        BackgroundColor = Color.FromArgb("#B3000000");

        var current = student is null ? vm.Policy.Web : student.OwnWebLimits;
        var savedAllow = Preferences.Default.Get(PrefAllow, "");
        var savedBlock = Preferences.Default.Get(PrefBlock, "");
        // First use: start from the list that's active now.
        if (savedAllow.Length == 0 && current is { WebMode: WebLimitMode.AllowOnlyListed }) savedAllow = string.Join(Environment.NewLine, current.WebHosts);
        if (savedBlock.Length == 0 && current is { WebMode: WebLimitMode.BlockListed }) savedBlock = string.Join(Environment.NewLine, current.WebHosts);

        var options = student is null ? new List<string> { NoLimits, UseBlock, UseAllow } : new List<string> { SameAsClass, NoLimits, UseBlock, UseAllow };
        var phone = DeviceInfo.Idiom == DeviceIdiom.Phone;
        _mode = phone
            ? new Picker { ItemsSource = options, HorizontalOptions = LayoutOptions.Fill }
            : new Picker { ItemsSource = options, WidthRequest = 360, HorizontalOptions = LayoutOptions.Start };
        _mode.SelectedItem = current is null ? SameAsClass : current.WebMode switch
        {
            WebLimitMode.AllowOnlyListed => UseAllow,
            WebLimitMode.BlockListed => UseBlock,
            _ => NoLimits,
        };

        _allow = ListEditor(savedAllow, "One site per line, e.g.\ndocs.google.com\nwikipedia.org");
        _block = ListEditor(savedBlock, "One site per line, e.g.\nyoutube.com\ntiktok.com");

        // Side by side; one above the other on phones (two lists in ~150 px each are too narrow).
        var lists = new Grid
        {
            ColumnDefinitions = phone
                ? [new ColumnDefinition(GridLength.Star)]
                : [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
            RowDefinitions = phone ? [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)] : [new RowDefinition(GridLength.Auto)],
            ColumnSpacing = 12,
            RowSpacing = 10,
        };
        lists.Add(new VerticalStackLayout { Spacing = 4, Children = { Heading("Allow list (whitelist)"), _allow } }, 0, 0);
        lists.Add(new VerticalStackLayout { Spacing = 4, Children = { Heading("Block list (blacklist)"), _block } }, phone ? 0 : 1, phone ? 1 : 0);

        var apply = new Button { Text = "Apply", Padding = new Thickness(18, 4), BackgroundColor = Color.FromArgb("#7E57C2"), TextColor = Colors.White };
        apply.Clicked += async (_, _) => await ApplyAsync();
        var cancel = new Button { Text = "Cancel", Padding = new Thickness(14, 4), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White };
        cancel.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var title = student is null ? "Websites for the whole class" : $"Websites for {student.Header}";
        var intro = student is null
            ? "Choose which list applies to every student PC. Students with their own list keep it."
            : "Give this student their own list, or keep them on the class's.";

        var body = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                new Label { Text = title, FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#B794F6") },
                new Label { Text = intro, FontSize = 12, TextColor = Color.FromArgb("#D0D3D8"), LineBreakMode = LineBreakMode.WordWrap },
                _mode,
                lists,
                new Label
                {
                    Text = "A site also covers its sub-domains (youtube.com includes m.youtube.com). Both lists are saved on this PC for next time. " +
                           "Website limits stay on the student PCs until you change them, even when no teacher is connected. A blocked tab is closed and the student sees why.",
                    FontSize = 11, TextColor = Color.FromArgb("#9AA0A6"), LineBreakMode = LineBreakMode.WordWrap,
                },
                _error,
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { cancel, apply } },
            },
        };

        Content = new Grid
        {
            Children =
            {
                new Border
                {
                    BackgroundColor = Color.FromArgb("#1E2027"),
                    Stroke = Color.FromArgb("#B794F6"),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                    Padding = new Thickness(20, 16),
                    MaximumWidthRequest = 720,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(16),
                    Content = new ScrollView { Content = body },
                },
            },
        };
    }

    private async Task ApplyAsync()
    {
        _error.IsVisible = false;
        var choice = _mode.SelectedItem as string ?? NoLimits;
        var allow = Lines(_allow.Text);
        var block = Lines(_block.Text);

        // Keep both lists for next time, whichever one is used now.
        Preferences.Default.Set(PrefAllow, string.Join(Environment.NewLine, allow));
        Preferences.Default.Set(PrefBlock, string.Join(Environment.NewLine, block));

        WebLimits? limits = choice switch
        {
            SameAsClass => null,
            UseAllow => new WebLimits { WebMode = WebLimitMode.AllowOnlyListed, WebHosts = allow },
            UseBlock => new WebLimits { WebMode = WebLimitMode.BlockListed, WebHosts = block },
            _ => new WebLimits { WebMode = WebLimitMode.Off },
        };

        if (limits is not null)
        {
            var bad = limits.WebHosts.Where(h => ClassroomLimits.CleanHost(h) is null).ToArray();
            if (bad.Length > 0) { ShowError($"Not a site: {string.Join(", ", bad)}."); return; }
            if (limits.WebMode == WebLimitMode.AllowOnlyListed && limits.WebHosts.Count == 0
                && !await DisplayAlertAsync("Block every website?", "The allow list is empty, so every website would be blocked.", "Block all", "Cancel"))
                return;
        }

        if (!_vm.IsConnected) { ShowError("Not connected to the classroom server."); return; }

        var error = _student is null
            ? await _vm.SetPolicyAsync(_vm.Policy.WithWeb(limits))
            : await _vm.SetStudentWebLimitsAsync([_student.ConnectionId], limits);
        if (error is not null) { ShowError(error); return; }
        await Navigation.PopModalAsync();
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private static Label Heading(string text) => new() { Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Colors.White };

    private static Editor ListEditor(string text, string placeholder) => new()
    {
        Text = text, Placeholder = placeholder, HeightRequest = 170, AutoSize = EditorAutoSizeOption.Disabled, FontSize = 12,
    };

    private static string[] Lines(string? text) =>
        (text ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected override bool OnBackButtonPressed()
    {
        _ = Navigation.PopModalAsync();
        return true;
    }
}
