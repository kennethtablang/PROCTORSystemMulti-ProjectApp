using PROCTOR.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Settings → Lock screen: the heading, message and background colour students see when their
/// screen is locked, with a live preview. Saved on this device and used by every Lock button.
/// </summary>
public sealed class LockScreenPage : ContentPage
{
    private static readonly Color Muted = Color.FromArgb("#9AA0A6");

    private readonly Entry _title;
    private readonly Editor _message;
    private readonly Entry _hex;
    private readonly Border _preview;
    private readonly Label _previewTitle;
    private readonly Label _previewMessage;
    private readonly Label _error = new() { FontSize = 12, TextColor = Color.FromArgb("#EF9A9A"), IsVisible = false };
    private string _color;

    public LockScreenPage()
    {
        BackgroundColor = Color.FromArgb("#B3000000");
        _color = LockLook.Color;

        _title = new Entry { Text = LockLook.Title, MaxLength = ClassroomLimits.MaxLockTitleLength, Placeholder = ClassroomLimits.DefaultLockTitle };
        _title.TextChanged += (_, _) => UpdatePreview();
        _message = new Editor
        {
            Text = LockLook.Message, MaxLength = ClassroomLimits.MaxTextLength, Placeholder = ClassroomLimits.DefaultLockMessage,
            HeightRequest = 80, AutoSize = EditorAutoSizeOption.Disabled,
        };
        _message.TextChanged += (_, _) => UpdatePreview();

        // Colour: ready-made swatches, or any #RRGGBB.
        var swatches = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        foreach (var (name, color) in LockLook.Swatches)
        {
            var swatch = new Button
            {
                BackgroundColor = Color.FromArgb(color),
                WidthRequest = 36, HeightRequest = 36, CornerRadius = 18,
                BorderColor = Color.FromArgb("#5F6368"), BorderWidth = 1,
                Margin = new Thickness(0, 0, 8, 8), Padding = 0,
            };
            ToolTipProperties.SetText(swatch, name);
            SemanticProperties.SetDescription(swatch, name);
            swatch.Clicked += (_, _) => { _color = color; _hex!.Text = color; UpdatePreview(); };
            swatches.Children.Add(swatch);
        }
        _hex = new Entry { Text = _color, MaxLength = 7, Placeholder = "#14121E", WidthRequest = 120, HorizontalOptions = LayoutOptions.Start };
        _hex.TextChanged += (_, e) =>
        {
            if (ClassroomLimits.CleanColor(e.NewTextValue) is { } c) { _color = c; _error.IsVisible = false; UpdatePreview(); }
        };

        // Preview: a small copy of the student's lock screen.
        _previewTitle = new Label { FontSize = 22, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap };
        _previewMessage = new Label { FontSize = 13, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap, Margin = new Thickness(0, 8, 0, 0) };
        _preview = new Border
        {
            StrokeThickness = 1,
            Stroke = Color.FromArgb("#3A3E47"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            HeightRequest = 170,
            Padding = new Thickness(16),
            Content = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { _previewTitle, _previewMessage } },
        };

        var save = new Button { Text = "Save", Padding = new Thickness(18, 4), BackgroundColor = Color.FromArgb("#7E57C2"), TextColor = Colors.White };
        save.Clicked += async (_, _) => await SaveAsync();
        var cancel = new Button { Text = "Cancel", Padding = new Thickness(14, 4), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White };
        cancel.Clicked += async (_, _) => await Navigation.PopModalAsync();
        var reset = new Button { Text = "Back to default", Padding = new Thickness(14, 4), BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#B794F6"), BorderColor = Color.FromArgb("#B794F6"), BorderWidth = 1 };
        reset.Clicked += (_, _) =>
        {
            _title.Text = ClassroomLimits.DefaultLockTitle;
            _message.Text = ClassroomLimits.DefaultLockMessage;
            _color = ClassroomLimits.DefaultLockColor;
            _hex.Text = _color;
            UpdatePreview();
        };

        var body = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "Lock screen", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#B794F6") },
                new Label
                {
                    Text = "What students see when you lock their screens. Saved on this device and used every time you press Lock.",
                    FontSize = 12, TextColor = Color.FromArgb("#D0D3D8"), LineBreakMode = LineBreakMode.WordWrap,
                },
                Caption("Heading"), _title,
                Caption("Message"), _message,
                Caption("Background colour"), swatches,
                new HorizontalStackLayout { Spacing = 8, Children = { new Label { Text = "or a colour code:", FontSize = 12, TextColor = Muted, VerticalOptions = LayoutOptions.Center }, _hex } },
                Caption("Preview"), _preview,
                _error,
                new FlexLayout
                {
                    Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                    JustifyContent = Microsoft.Maui.Layouts.FlexJustify.End,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { Gap(reset), Gap(cancel), Gap(save) },
                },
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
                    MaximumWidthRequest = 560,
                    MaximumHeightRequest = 760,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(16),
                    Content = new ScrollView { Content = body },
                },
            },
        };
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _preview.BackgroundColor = Color.FromArgb(_color);
        var light = ClassroomLimits.PrefersLightText(_color);
        _previewTitle.Text = string.IsNullOrWhiteSpace(_title.Text) ? ClassroomLimits.DefaultLockTitle : _title.Text.Trim();
        _previewTitle.TextColor = light ? Colors.White : Color.FromArgb("#111111");
        _previewMessage.Text = string.IsNullOrWhiteSpace(_message.Text) ? ClassroomLimits.DefaultLockMessage : _message.Text.Trim();
        _previewMessage.TextColor = light ? Color.FromArgb("#D9FFFFFF") : Color.FromArgb("#CC111111");
    }

    private async Task SaveAsync()
    {
        if (ClassroomLimits.CleanColor(_hex.Text) is null)
        {
            _error.Text = "The colour code must look like #1E4FBF.";
            _error.IsVisible = true;
            return;
        }
        LockLook.Title = _title.Text ?? "";
        LockLook.Message = _message.Text ?? "";
        LockLook.Color = _color;
        await Navigation.PopModalAsync();
    }

    private static Label Caption(string text) => new()
    {
        Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Muted, Margin = new Thickness(0, 6, 0, 0),
    };

    private static View Gap(View v) { v.Margin = new Thickness(8, 4, 0, 0); return v; }

    protected override bool OnBackButtonPressed()
    {
        _ = Navigation.PopModalAsync();
        return true;
    }
}
