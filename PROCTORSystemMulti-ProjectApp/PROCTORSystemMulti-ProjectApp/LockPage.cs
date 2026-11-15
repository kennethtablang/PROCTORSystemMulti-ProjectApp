using PROCTORSystemMulti_ProjectApp.ClassInsight;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>Full-window PIN prompt shown at launch and on "Lock this app now" (teacher secure mode).</summary>
public sealed class LockPage : ContentPage
{
    private readonly Entry _pin;
    private readonly Label _error;

    public LockPage()
    {
        Title = "Locked";
        BackgroundColor = Color.FromArgb("#15171C");
        _pin = new Entry
        {
            Placeholder = "Teacher PIN",
            IsPassword = true,
            Keyboard = Keyboard.Numeric,
            MaxLength = TeacherPin.MaxLength,
            WidthRequest = 220,
            HorizontalTextAlignment = TextAlignment.Center,
            FontSize = 20,
        };
        _pin.Completed += async (_, _) => await TryUnlock();
        _error = new Label { TextColor = Color.FromArgb("#EF9A9A"), IsVisible = false, HorizontalOptions = LayoutOptions.Center };
        var unlock = new Button { Text = "Unlock", WidthRequest = 220 };
        unlock.Clicked += async (_, _) => await TryUnlock();

        Content = new VerticalStackLayout
        {
            Spacing = 14,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "🔐 ClassInsight is locked", FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = "Enter the teacher PIN to continue.", TextColor = Color.FromArgb("#9AA0A6"), HorizontalOptions = LayoutOptions.Center },
                _pin,
                unlock,
                _error,
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _pin.Focus();
    }

    // The back button / Escape must not dismiss the lock.
    protected override bool OnBackButtonPressed() => true;

    private async Task TryUnlock()
    {
        _error.IsVisible = false;
        if (await TeacherPin.VerifyAsync(_pin.Text))
        {
            await Navigation.PopModalAsync();
            return;
        }
        _pin.Text = "";
        _error.Text = "That PIN isn't right.";
        _error.IsVisible = true;
        _pin.Focus();
    }
}
