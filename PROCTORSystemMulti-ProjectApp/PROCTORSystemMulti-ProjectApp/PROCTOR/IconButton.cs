using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Icon glyphs from Segoe MDL2 Assets, the icon font built into Windows 10 and 11 (no emoji: they
/// render differently per PC and look out of place in a work app).
/// </summary>
public static class Icons
{
    public const string FontFamily = "Segoe MDL2 Assets";

    public const string Lock = "";
    public const string Unlock = "";
    public const string Message = "";
    public const string Chat = "";
    public const string Globe = "";
    public const string Screen = "";
    public const string Poll = "";
    public const string Limits = "";
    public const string Power = "";
    public const string More = "";
    public const string Help = "";
    public const string Send = "";
    public const string Control = "";
    public const string Stop = "";
    public const string Camera = "";
    public const string Info = "";
    public const string ZoomOut = "";
    public const string ZoomIn = "";
    public const string Back = "";
    public const string Websites = "";
    public const string Timer = "";
    public const string Collect = "";
    public const string Shuffle = "";
    public const string Attendance = "";
    public const string Key = "";
    public const string SignOut = "";
    public const string Pane = "";
    public const string Settings = "";
}

/// <summary>
/// A compact icon-only button: <see cref="Glyph"/> is the icon, <see cref="ToolTip"/> the name shown
/// on hover and read by screen readers (there is no text on the button itself).
/// </summary>
public class IconButton : Button
{
    public static readonly BindableProperty GlyphProperty = BindableProperty.Create(
        nameof(Glyph), typeof(string), typeof(IconButton), string.Empty,
        propertyChanged: (b, _, _) => ((IconButton)b).UpdateIcon());

    public static readonly BindableProperty ToolTipProperty = BindableProperty.Create(
        nameof(ToolTip), typeof(string), typeof(IconButton), string.Empty,
        propertyChanged: (b, _, value) =>
        {
            ToolTipProperties.SetText(b, value);
            SemanticProperties.SetDescription(b, value as string ?? string.Empty);
        });

    public static readonly BindableProperty IconColorProperty = BindableProperty.Create(
        nameof(IconColor), typeof(Color), typeof(IconButton), null,
        propertyChanged: (b, _, _) => ((IconButton)b).UpdateIcon());

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string ToolTip { get => (string)GetValue(ToolTipProperty); set => SetValue(ToolTipProperty, value); }
    /// <summary>Icon colour; by default the theme's button text colour.</summary>
    public Color? IconColor { get => (Color?)GetValue(IconColorProperty); set => SetValue(IconColorProperty, value); }

    public IconButton()
    {
        Padding = new Thickness(0);
        WidthRequest = 30;
        HeightRequest = 26;
        MinimumWidthRequest = 26;
        MinimumHeightRequest = 24;
        UpdateIcon();
        if (Application.Current is { } app) app.RequestedThemeChanged += (_, _) => UpdateIcon();
    }

    // The icon is the button's text in the icon font. (A FontImageSource from a system font that
    // isn't registered with MAUI renders blank on Windows; text in that font renders fine.)
    private void UpdateIcon()
    {
        FontFamily = Icons.FontFamily;
        FontSize = 14;
        Text = Glyph ?? string.Empty;
        TextColor = IconColor ?? DefaultColor;
    }

    // Matches the implicit Button style's text colour (Styles.xaml).
    private static Color DefaultColor =>
        Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#242424") : Colors.White;
}
