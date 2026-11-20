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

    /// <summary>
    /// The card: title, a line of explanation, then <paramref name="rows"/>. The LAST row is the footer
    /// (the buttons): it always stays visible at the bottom. Everything between scrolls when it doesn't
    /// fit, and rows marked Fill (lists, the history picture) get most of the card's height. The card
    /// is sized from the window (never wider or taller than it), so nothing is cut off on any screen.
    /// </summary>
    public static View Page(ContentPage page, string title, string intro, double maxWidth, params (View View, bool Fill)[] rows)
    {
        var body = new VerticalStackLayout { Spacing = 8 };
        body.Children.Add(new Label { Text = intro, FontSize = 12, TextColor = Body, LineBreakMode = LineBreakMode.WordWrap });
        var fills = new List<View>();
        for (var i = 0; i < rows.Length - 1; i++)
        {
            body.Children.Add(rows[i].View);
            if (rows[i].Fill) fills.Add(rows[i].View);
        }

        var grid = new Grid
        {
            RowSpacing = 10,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
        };
        grid.Add(new Label { Text = title, FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Accent }, 0, 0);
        grid.Add(new ScrollView { Content = body, Orientation = ScrollOrientation.Vertical }, 0, 1);
        if (rows.Length > 0) grid.Add(rows[^1].View, 0, 2);

        var card = CardBorder(grid);
        Fit(page, card, maxWidth, fullHeight: true, fills);
        return new Grid { Children = { card } };
    }

    /// <summary>The dark card itself (centred; its size is set by <see cref="Fit"/>).</summary>
    public static Border CardBorder(View content, Color? stroke = null) => new()
    {
        BackgroundColor = Card,
        Stroke = stroke ?? Accent,
        StrokeThickness = 1,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
        Padding = new Thickness(20, 16),
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        Content = content,
    };

    /// <summary>
    /// Keep a pop-up card inside the window: as wide as <paramref name="maxWidth"/> or the window
    /// allows, and either the window's full height (<paramref name="fullHeight"/>) or at most it.
    /// Explicit sizes, so the card can't grow past the window's edge to fit its content.
    /// </summary>
    public static void Fit(ContentPage page, Border card, double maxWidth, bool fullHeight, IReadOnlyList<View>? fills = null)
    {
        card.MaximumWidthRequest = double.PositiveInfinity;
        card.MaximumHeightRequest = double.PositiveInfinity;
        void Apply()
        {
            if (page.Width <= 0 || page.Height <= 0) return;
            var gutter = page.Width < 600 ? 8 : 16;
            var width = Math.Max(200, Math.Min(maxWidth, page.Width - 2 * gutter));
            var height = Math.Max(200, page.Height - 2 * gutter - 8);
            card.WidthRequest = width;
            if (fullHeight) card.HeightRequest = height;
            else card.MaximumHeightRequest = height;
            // Lists get most of the card; with little room the whole middle part scrolls instead.
            if (fills is { Count: > 0 })
            {
                var each = Math.Max(220, (height - 220) / fills.Count);
                foreach (var f in fills) f.HeightRequest = each;
            }
        }
        page.SizeChanged += (_, _) => Apply();
        Apply();
    }

    /// <summary>Fit the card (the first Border in the page) of a pop-up built by hand to the window.</summary>
    public static void FitCard(ContentPage page, double maxWidth)
    {
        var card = page.Content as Border ?? (page.Content as Layout)?.Children.OfType<Border>().FirstOrDefault();
        if (card is null) return;
        card.HorizontalOptions = LayoutOptions.Center;
        card.VerticalOptions = LayoutOptions.Center;
        card.Margin = new Thickness(0);
        Fit(page, card, maxWidth, fullHeight: false);
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
