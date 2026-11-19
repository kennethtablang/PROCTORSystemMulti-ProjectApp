using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Icon glyphs (no emoji: they render differently per device and look out of place in a work app).
/// Windows uses Segoe MDL2 Assets, the icon font built into Windows 10 and 11. Other devices don't
/// have it (the buttons would be blank), so they use Google's Material Icons, bundled with the app
/// (Resources\Fonts, Apache 2.0, licence in Resources\Raw\licenses).
/// </summary>
public static class Icons
{
    private static readonly bool Segoe = OperatingSystem.IsWindows();

    /// <summary>The icon font for this device, for labels that show a glyph.</summary>
    public static readonly string FontFamily = Segoe ? "Segoe MDL2 Assets" : "MaterialIcons";

    private static string G(char segoe, char material) => (Segoe ? segoe : material).ToString();

    public static readonly string Lock = G('', '');        // lock
    public static readonly string Unlock = G('', '');      // lock_open
    public static readonly string Message = G('', '');     // message
    public static readonly string Chat = G('', '');        // chat
    public static readonly string Globe = G('', '');       // language
    public static readonly string Screen = G('', '');      // screen_share
    public static readonly string Poll = G('', '');        // poll
    public static readonly string Limits = G('', '');      // shield
    public static readonly string Power = G('', '');       // power_settings_new
    public static readonly string More = G('', '');        // more_horiz
    public static readonly string Help = G('', '');        // help_outline
    public static readonly string Send = G('', '');        // send
    public static readonly string Control = G('', '');     // mouse
    public static readonly string Stop = G('', '');        // stop
    public static readonly string Camera = G('', '');      // photo_camera
    public static readonly string Info = G('', '');        // info_outline
    public static readonly string ZoomOut = G('', '');     // zoom_out
    public static readonly string ZoomIn = G('', '');      // zoom_in
    public static readonly string Back = G('', '');        // arrow_back
    public static readonly string Websites = G('', '');    // filter_list
    public static readonly string Timer = G('', '');       // timer
    public static readonly string Collect = G('', '');     // download
    public static readonly string Shuffle = G('', '');     // shuffle
    public static readonly string Attendance = G('', '');  // how_to_reg
    public static readonly string Key = G('', '');         // vpn_key
    public static readonly string SignOut = G('', '');     // logout
    public static readonly string Pane = G('', '');        // view_sidebar
    public static readonly string Settings = G('', '');    // settings
    public static readonly string Keyboard = G('', '');    // keyboard
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
        // Mouse-sized on desktop; finger-sized on phones and tablets.
        var touch = DeviceInfo.Idiom == DeviceIdiom.Phone || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        WidthRequest = touch ? 40 : 30;
        HeightRequest = touch ? 36 : 26;
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
        FontSize = OperatingSystem.IsWindows() ? 14 : 18;   // Material Icons are drawn for larger sizes
        Text = Glyph ?? string.Empty;
        TextColor = IconColor ?? DefaultColor;
    }

    // Matches the implicit Button style's text colour (Styles.xaml).
    private static Color DefaultColor =>
        Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#242424") : Colors.White;
}
