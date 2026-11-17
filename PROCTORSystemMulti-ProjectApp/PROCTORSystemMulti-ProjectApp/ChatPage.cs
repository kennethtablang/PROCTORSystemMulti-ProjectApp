using PROCTORSystemMulti_ProjectApp.PROCTOR;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>Teacher ↔ one student chat.</summary>
public sealed class ChatPage : ContentPage
{
    private readonly DashboardViewModel _vm;
    private readonly StudentTile _tile;
    private readonly Entry _input;
    private readonly CollectionView _list;
    private readonly Label _error;

    public ChatPage(DashboardViewModel vm, StudentTile tile)
    {
        _vm = vm;
        _tile = tile;
        Title = $"Chat · {tile.MachineName}";

        var lines = vm.ChatFor(tile.ConnectionId);
        _list = new CollectionView
        {
            ItemsSource = lines,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepLastItemInView,
            ItemTemplate = new DataTemplate(() =>
            {
                var who = new Label { FontSize = 11, TextColor = Color.FromArgb("#9AA0A6") };
                who.SetBinding(Label.TextProperty, nameof(ChatLine.Caption));
                var text = new Label { FontSize = 14, TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap };
                text.SetBinding(Label.TextProperty, nameof(ChatLine.Text));
                var bubble = new Border
                {
                    Padding = new Thickness(10, 6),
                    StrokeThickness = 0,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                    Content = new VerticalStackLayout { Spacing = 2, Children = { who, text } },
                };
                bubble.SetBinding(BackgroundColorProperty, nameof(ChatLine.Background));
                bubble.SetBinding(View.HorizontalOptionsProperty, nameof(ChatLine.Alignment));
                bubble.SetBinding(View.MarginProperty, nameof(ChatLine.Indent));
                return new ContentView { Padding = new Thickness(0, 3), Content = bubble };
            }),
            EmptyView = new Label
            {
                Text = $"No messages yet. Write to {tile.MachineName} below — it opens a chat window on their screen.",
                TextColor = Color.FromArgb("#9AA0A6"),
                HorizontalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(20),
            },
        };

        _input = new Entry { Placeholder = "Type a message…", MaxLength = global::PROCTOR.Shared.Contracts.ClassroomLimits.MaxTextLength, ReturnType = ReturnType.Send };
        _input.Completed += async (_, _) => await SendAsync();
        var send = new Button { Text = "Send", Padding = new Thickness(16, 4) };
        send.Clicked += async (_, _) => await SendAsync();
        var close = new Button { Text = "Close", Padding = new Thickness(16, 4) };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        _error = new Label { TextColor = Color.FromArgb("#EF9A9A"), IsVisible = false };

        var header = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            Children = { new Label { Text = $"💬 {tile.Header}", FontSize = 18, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center } },
        };
        header.Add(close, 1, 0);

        var inputRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        inputRow.Add(_input, 0, 0);
        inputRow.Add(send, 1, 0);

        var root = new Grid
        {
            Padding = new Thickness(16),
            RowSpacing = 10,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
        };
        root.Add(header, 0, 0);
        root.Add(_list, 0, 1);
        root.Add(_error, 0, 2);
        root.Add(inputRow, 0, 3);
        Content = root;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.OpenChat = _tile.ConnectionId;
        _tile.UnreadChat = 0;
        _input.Focus();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (_vm.OpenChat == _tile.ConnectionId) _vm.OpenChat = null;
    }

    private async Task SendAsync()
    {
        var text = _input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _error.IsVisible = false;
        var error = await _vm.SendChatAsync(_tile.ConnectionId, text);
        if (error is null)
        {
            _input.Text = "";
            return;
        }
        _error.Text = error;
        _error.IsVisible = true;
    }
}
