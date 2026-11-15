using System.ComponentModel;
using ClassInsight.Shared.Contracts;
using PROCTORSystemMulti_ProjectApp.ClassInsight;

namespace PROCTORSystemMulti_ProjectApp;

/// <summary>Create a poll for the class and watch the answers arrive live (P5, polls/voting).</summary>
public partial class PollPage : ContentPage
{
    private readonly DashboardViewModel _vm;

    public PollPage(DashboardViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Kind.SelectedIndex = 0;
        Render(vm.PollResults);
        vm.PropertyChanged += OnVmChanged;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.PollResults)) Render(_vm.PollResults);
    }

    private void OnKindChanged(object? sender, EventArgs e) =>
        Options.IsVisible = Kind.SelectedIndex == (int)PollKind.MultipleChoice;

    private async void OnStart(object? sender, EventArgs e)
    {
        var poll = new Poll
        {
            Question = Question.Text ?? "",
            Kind = (PollKind)Math.Max(0, Kind.SelectedIndex),
            Options = (Options.Text ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Anonymous = Anonymous.IsChecked,
        };
        if (PollLimits.Clean(poll, out var problem) is null) { ShowError(problem!); return; }
        if (!_vm.IsConnected) { ShowError("Not connected to the classroom server."); return; }

        var error = await _vm.StartPollAsync(poll);
        if (error is not null) { ShowError(error); return; }
        Error.IsVisible = false;
        Question.Text = "";
        Options.Text = "";
    }

    private async void OnClosePoll(object? sender, EventArgs e)
    {
        var error = await _vm.ClosePollAsync();
        if (error is not null) ShowError(error);
    }

    private async void OnDone(object? sender, EventArgs e) => await Navigation.PopModalAsync();

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.IsVisible = true;
    }

    /// <summary>Draw the live tally: a bar per option (or the written answers), and who said what.</summary>
    private void Render(PollResults? r)
    {
        ResultsCard.IsVisible = r is not null;
        if (r is null) return;

        ResultQuestion.Text = r.Poll.Question;
        ResultStatus.Text = $"{(r.Closed ? "Closed" : "Open")} · {r.Answered} of {r.Asked} answered" +
                            (r.Poll.Anonymous ? " · anonymous" : "");
        CloseButton.IsVisible = !r.Closed;

        Bars.Children.Clear();
        var options = PollLimits.EffectiveOptions(r.Poll);
        for (var i = 0; i < options.Count; i++)
        {
            var count = i < r.Counts.Count ? r.Counts[i] : 0;
            var share = r.Answered == 0 ? 0 : (double)count / r.Answered;
            var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 10 };
            row.Add(new Label { Text = options[i], FontSize = 14 }, 0, 0);
            row.Add(new Label { Text = $"{count} ({share:P0})", FontSize = 13, TextColor = Colors.Gray }, 1, 0);
            var bar = new ProgressBar { Progress = share, ProgressColor = Color.FromArgb("#7E57C2"), HeightRequest = 10 };
            row.Add(bar, 0, 1);
            Grid.SetColumnSpan(bar, 2);
            Bars.Children.Add(row);
        }

        Responses.Children.Clear();
        ResponsesHeader.IsVisible = r.Responses.Count > 0;
        ResponsesHeader.Text = r.Poll.Anonymous ? "Answers (anonymous)" : "Answers";
        foreach (var resp in r.Responses)
        {
            var answer = resp.Text ?? (resp.Choice >= 0 && resp.Choice < options.Count ? options[resp.Choice] : "?");
            Responses.Children.Add(new Label
            {
                Text = string.IsNullOrEmpty(resp.Student) ? $"• {answer}" : $"{resp.Student} — {answer}",
                FontSize = 13,
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }
    }
}
