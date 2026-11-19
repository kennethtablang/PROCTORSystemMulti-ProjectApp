namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Light / dark mode for the teacher app: follow the device (default), or always light or dark.
/// The choice is remembered on this device and applied when the app starts.
/// </summary>
public static class Appearance
{
    private const string Pref = "proctor.theme";

    /// <summary>Settings choices, in order: device, light, dark.</summary>
    public static IReadOnlyList<string> Choices { get; } = ["Match this device", "Light", "Dark"];

    /// <summary>The saved choice: <see cref="AppTheme.Unspecified"/> follows the device.</summary>
    public static AppTheme Saved
    {
        get
        {
            try
            {
                return Preferences.Default.Get(Pref, "") switch
                {
                    "light" => AppTheme.Light,
                    "dark" => AppTheme.Dark,
                    _ => AppTheme.Unspecified,
                };
            }
            catch { return AppTheme.Unspecified; }
        }
    }

    public static int SavedIndex => Saved switch { AppTheme.Light => 1, AppTheme.Dark => 2, _ => 0 };

    /// <summary>What the app looks like right now (the choice, or the device's theme).</summary>
    public static bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;

    /// <summary>Apply the saved choice (at start-up).</summary>
    public static void Apply()
    {
        if (Application.Current is { } app) app.UserAppTheme = Saved;
    }

    public static void Set(AppTheme theme)
    {
        try
        {
            Preferences.Default.Set(Pref, theme switch { AppTheme.Light => "light", AppTheme.Dark => "dark", _ => "" });
        }
        catch { /* not remembered, but still applied */ }
        if (Application.Current is { } app) app.UserAppTheme = theme;
    }

    public static void SetIndex(int index) => Set(index switch { 1 => AppTheme.Light, 2 => AppTheme.Dark, _ => AppTheme.Unspecified });

    /// <summary>The toolbar button: switch between light and dark.</summary>
    public static void Toggle() => Set(IsDark ? AppTheme.Light : AppTheme.Dark);
}
