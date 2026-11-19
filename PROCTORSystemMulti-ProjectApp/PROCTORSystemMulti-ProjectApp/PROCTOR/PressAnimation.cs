namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Visible feedback when a button is pressed: it shrinks a little and dims while held, then springs
/// back on release, so a tap or click clearly "lands" (also on touch screens, where there's no hover).
/// Attached to every <see cref="Button"/> in the app, icon buttons included (MauiProgramExtensions).
/// </summary>
public static class PressAnimation
{
    private const double PressedScale = 0.88;
    private const double PressedOpacity = 0.75;

    private static readonly BindableProperty AttachedProperty =
        BindableProperty.CreateAttached("PressAnimationAttached", typeof(bool), typeof(PressAnimation), false);

    public static void Attach(Button button)
    {
        if ((bool)button.GetValue(AttachedProperty)) return;
        button.SetValue(AttachedProperty, true);
        button.Pressed += OnPressed;
        button.Released += OnReleased;
    }

    private static async void OnPressed(object? sender, EventArgs e)
    {
        if (sender is not Button b || !b.IsEnabled) return;
        try
        {
            b.CancelAnimations();
            await Task.WhenAll(
                b.ScaleToAsync(PressedScale, 70, Easing.CubicOut),
                b.FadeToAsync(PressedOpacity, 70, Easing.CubicOut));
        }
        catch { /* the button went away mid-animation */ }
    }

    private static async void OnReleased(object? sender, EventArgs e)
    {
        if (sender is not Button b) return;
        try
        {
            b.CancelAnimations();
            await Task.WhenAll(
                b.ScaleToAsync(1, 220, Easing.SpringOut),
                b.FadeToAsync(1, 150, Easing.CubicOut));
        }
        catch
        {
            b.Scale = 1;
            b.Opacity = 1;
        }
    }
}
