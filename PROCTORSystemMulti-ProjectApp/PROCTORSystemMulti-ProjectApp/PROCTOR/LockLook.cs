using PROCTOR.Shared.Contracts;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// What students see when their screen is locked: heading, message and background colour. Set in
/// Settings → Lock screen, remembered on this device, and sent with every lock.
/// </summary>
public static class LockLook
{
    private const string PrefTitle = "proctor.lock.title";
    private const string PrefMessage = "proctor.lock.message";
    private const string PrefColor = "proctor.lock.color";

    /// <summary>Ready-made backgrounds (name, colour); the teacher can also type any "#RRGGBB".</summary>
    public static IReadOnlyList<(string Name, string Color)> Swatches { get; } =
    [
        ("Night purple", ClassroomLimits.DefaultLockColor),
        ("Black", "#000000"),
        ("Charcoal", "#2B2D31"),
        ("Navy", "#0D2B4E"),
        ("Royal blue", "#1E4FBF"),
        ("Teal", "#0B5D5E"),
        ("Forest", "#1B4D2E"),
        ("Maroon", "#5C1222"),
        ("Red", "#B3261E"),
        ("Orange", "#C2560C"),
        ("Gold", "#F2C230"),
        ("White", "#FFFFFF"),
    ];

    public static string Title
    {
        get => Get(PrefTitle) is { Length: > 0 } t ? t : ClassroomLimits.DefaultLockTitle;
        set => Set(PrefTitle, ClassroomLimits.CleanLockTitle(value) ?? "");
    }

    public static string Message
    {
        get => Get(PrefMessage) is { Length: > 0 } m ? m : ClassroomLimits.DefaultLockMessage;
        set => Set(PrefMessage, ClassroomLimits.CleanText(value) ?? "");
    }

    public static string Color
    {
        get => ClassroomLimits.CleanColor(Get(PrefColor)) ?? ClassroomLimits.DefaultLockColor;
        set => Set(PrefColor, ClassroomLimits.CleanColor(value) ?? "");
    }

    public static void Reset()
    {
        Set(PrefTitle, "");
        Set(PrefMessage, "");
        Set(PrefColor, "");
    }

    private static string Get(string key)
    {
        try { return Preferences.Default.Get(key, ""); } catch { return ""; }
    }

    private static void Set(string key, string value)
    {
        try { Preferences.Default.Set(key, value); } catch { /* not remembered, still used this time */ }
    }
}
