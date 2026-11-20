using Microsoft.Maui.Layouts;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>The look shared by the pop-up pages (alerts, screen history, PC inventory): a dark card over the dashboard.</summary>
internal static class ModalUi
{
    public static readonly Color Accent = Color.FromArgb("#B794F6");
    public static readonly Color Muted = Color.FromArgb("#9AA0A6");
    public static readonly Color Body = Color.FromArgb("#D0D3D8");
    public static readonly Color Card = Color.FromArgb("#1E2027");
    public static readonly Color Overlay = Color.FromArgb("#B3000000");

    /// <summary>The card: title, a line of explanation, then <paramref name="rows"/> (the one marked Star takes the rest).</summary>
    public static View Page(string title, string intro, double maxWidth, params (View View, bool Fill)[] rows)
    {
        var grid = new Grid { RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(new Label { Text = title, FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Accent }, 0, 0);
        grid.Add(new Label { Text = intro, FontSize = 12, TextColor = Body, LineBreakMode = LineBreakMode.WordWrap }, 0, 1);
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(rows[i].Fill ? GridLength.Star : GridLength.Auto));
            grid.Add(rows[i].View, 0, i + 2);
        }

        return new Grid
        {
            Children =
            {
                new Border
                {
                    BackgroundColor = Card,
                    Stroke = Accent,
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                    Padding = new Thickness(20, 16),
                    MaximumWidthRequest = maxWidth,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                    Margin = new Thickness(16, 24),
                    Content = grid,
                },
            },
        };
    }

    public static Button Primary(string text) =>
        new() { Text = text, Padding = new Thickness(14, 4), BackgroundColor = Color.FromArgb("#7E57C2"), TextColor = Colors.White };

    public static Button Secondary(string text) =>
        new() { Text = text, Padding = new Thickness(14, 4), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White };

    public static Button Danger(string text) =>
        new() { Text = text, Padding = new Thickness(14, 4), BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#EF9A9A"), BorderColor = Color.FromArgb("#EF9A9A"), BorderWidth = 1 };

    /// <summary>A wrapping row (phones get several lines); <paramref name="end"/> right-aligns it.</summary>
    public static FlexLayout Row(bool end, params View[] views)
    {
        var row = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Center,
            JustifyContent = end ? FlexJustify.End : FlexJustify.Start,
        };
        foreach (var v in views)
        {
            v.Margin = new Thickness(0, 0, 8, 4);
            row.Children.Add(v);
        }
        return row;
    }

    public static Label Text(string text, double size = 12, Color? color = null) =>
        new() { Text = text, FontSize = size, TextColor = color ?? Body, VerticalOptions = LayoutOptions.Center };

    /// <summary>A tick box with its label (tapping the label ticks it too); <paramref name="changed"/> runs when it changes.</summary>
    public static View Toggle(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = value, Color = Accent, VerticalOptions = LayoutOptions.Center };
        box.CheckedChanged += (_, e) => changed(e.Value);
        var label = Text(text, 13, Colors.White);
        label.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => box.IsChecked = !box.IsChecked) });
        return new HorizontalStackLayout { Spacing = 0, Children = { box, label } };
    }

    public static bool Back(ContentPage page)
    {
        _ = page.Navigation.PopModalAsync();
        return true;
    }
}
