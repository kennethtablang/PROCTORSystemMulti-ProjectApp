using PROCTOR.Shared;
using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>
/// Settings (the gear beside "Switch room"): student screen quality and size, the teacher PIN, the
/// classroom this app is connected to, and version / updates. Changes apply at once and are
/// remembered on this device.
/// </summary>
public sealed class SettingsPage : ContentPage
{
    private static readonly Color Muted = Color.FromArgb("#9AA0A6");
    private readonly DashboardViewModel _vm;
    private readonly Label _pinState = new() { FontSize = 12, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center };

    public SettingsPage(DashboardViewModel vm)
    {
        _vm = vm;
        BackgroundColor = Color.FromArgb("#B3000000");

        // ---- student screens
        var quality = new Picker { ItemsSource = vm.ThumbnailChoices.ToList(), SelectedIndex = vm.ThumbnailSizeIndex, WidthRequest = 220 };
        quality.SelectedIndexChanged += (_, _) => vm.ThumbnailSizeIndex = quality.SelectedIndex;
        var focus = new Picker { ItemsSource = vm.FocusChoices.ToList(), SelectedIndex = vm.FocusResolutionIndex, WidthRequest = 220 };
        focus.SelectedIndexChanged += (_, _) => vm.FocusResolutionIndex = focus.SelectedIndex;
        var size = new Slider { Minimum = DashboardViewModel.MinTileSize, Maximum = DashboardViewModel.MaxTileSize, Value = vm.TileSize, WidthRequest = 220 };
        size.ValueChanged += (_, e) => vm.TileSize = e.NewValue;

        // ---- teacher PIN
        var pin = Secondary("Set or change PIN");
        pin.Clicked += async (_, _) => { await TeacherPinFlow.ManageAsync(this); RefreshPin(); };
        var lockNow = Secondary("Lock this app now");
        lockNow.Clicked += async (_, _) => await TeacherPinFlow.LockNowAsync(this);
        RefreshPin();

        // ---- classroom
        var server = vm.Client.CurrentServer;
        var switchRoom = Secondary("Switch room");
        switchRoom.IsEnabled = vm.IsConnected;
        switchRoom.Clicked += async (_, _) =>
        {
            await Navigation.PopModalAsync();
            if (vm.SwitchRoomCommand.CanExecute(null)) vm.SwitchRoomCommand.Execute(null);
        };

        // ---- about
        var updates = Secondary("Check for updates");
        updates.Clicked += async (_, _) => { await Navigation.PopModalAsync(); await Shell.Current.Navigation.PushModalAsync(new AboutPage()); };

        var close = new Button { Text = "Close", Padding = new Thickness(16, 4), BackgroundColor = Color.FromArgb("#7E57C2"), TextColor = Colors.White };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var body = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "Settings", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#B794F6") },

                Section("Student screens"),
                Row("Dashboard stream quality", quality),
                Row("Student view quality", focus),
                Row("Screen size on the dashboard", size),

                Section("Teacher PIN"),
                Row("PIN", _pinState),
                new HorizontalStackLayout { Spacing = 8, Children = { pin, lockNow } },

                Section("Classroom"),
                Row("Room", Value(server is null ? "Not connected" : server.RoomName)),
                Row("Address", Value(vm.StudentServerAddress)),
                Row("Server key", Value(vm.HasServerKey ? vm.ServerKey : "— (shown when connected)")),
                new HorizontalStackLayout { Spacing = 8, Children = { switchRoom } },

                Section("Class controls"),
                new Label
                {
                    Text = "Every control works without a join code on this classroom. To keep remote control, power and " +
                           "internet controls for teachers with a code, set \"AllowStrongControlsInOpenMode\": false in " +
                           "C:\\ProgramData\\PROCTOR\\Server\\appsettings.json on the teacher PC and restart the PROCTOR Classroom Service.",
                    FontSize = 11, TextColor = Muted, LineBreakMode = LineBreakMode.WordWrap,
                },

                Section("About"),
                Row("Version", Value(ProductVersion.Text)),
                new HorizontalStackLayout { Spacing = 8, Children = { updates } },

                new HorizontalStackLayout { HorizontalOptions = LayoutOptions.End, Margin = new Thickness(0, 8, 0, 0), Children = { close } },
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
                    MaximumHeightRequest = 720,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(16),
                    Content = new ScrollView { Content = body },
                },
            },
        };
    }

    private void RefreshPin() => _pinState.Text = TeacherPin.IsSet ? "Set (asked when the app opens)" : "Not set";

    private static Label Section(string text) => new()
    {
        Text = text, FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Muted, Margin = new Thickness(0, 10, 0, 0),
    };

    private static View Row(string label, View control)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(new GridLength(200)), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 10 };
        grid.Add(new Label { Text = label, FontSize = 12, TextColor = Colors.White, VerticalOptions = LayoutOptions.Center }, 0, 0);
        grid.Add(control, 1, 0);
        return grid;
    }

    private static Label Value(string text) => new()
    {
        Text = text, FontSize = 12, TextColor = Colors.White, LineBreakMode = LineBreakMode.CharacterWrap, VerticalOptions = LayoutOptions.Center,
    };

    private static Button Secondary(string text) => new()
    {
        Text = text, Padding = new Thickness(12, 3), BackgroundColor = Color.FromArgb("#2A2D34"), TextColor = Colors.White,
    };

    protected override bool OnBackButtonPressed()
    {
        _ = Navigation.PopModalAsync();
        return true;
    }
}
