using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClassInsight.Tests;

public class PollLimitsTests
{
    [Fact]
    public void Polls_are_validated_and_normalised()
    {
        Assert.Null(PollLimits.Clean(new Poll { Question = "  " }, out var e1));
        Assert.NotNull(e1);
        Assert.Null(PollLimits.Clean(new Poll { Question = "Q", Kind = PollKind.MultipleChoice, Options = ["A", "a"] }, out _)); // duplicate
        var mc = PollLimits.Clean(new Poll { Question = " Pick ", Kind = PollKind.MultipleChoice, Options = ["A", "B", "", "C"] }, out _);
        Assert.Equal("Pick", mc!.Question);
        Assert.Equal(["A", "B", "C"], mc.Options);
        var yn = PollLimits.Clean(new Poll { Question = "OK?", Kind = PollKind.YesNo, Options = ["ignored"] }, out _);
        Assert.Empty(yn!.Options);
        Assert.Equal(["Yes", "No"], PollLimits.EffectiveOptions(yn));
    }

    [Fact]
    public void Answers_must_fit_their_poll()
    {
        var yn = new Poll { Id = "p", Question = "OK?", Kind = PollKind.YesNo };
        Assert.NotNull(PollLimits.CleanAnswer(yn, new PollAnswer { PollId = "p", Choice = 1 }));
        Assert.Null(PollLimits.CleanAnswer(yn, new PollAnswer { PollId = "p", Choice = 2 }));
        Assert.Null(PollLimits.CleanAnswer(yn, new PollAnswer { PollId = "other", Choice = 0 }));
        var txt = new Poll { Id = "t", Question = "Word?", Kind = PollKind.ShortText };
        Assert.Equal(PollLimits.MaxTextAnswerLength, PollLimits.CleanAnswer(txt, new PollAnswer { PollId = "t", Text = new string('x', 999) })!.Text!.Length);
        Assert.Null(PollLimits.CleanAnswer(txt, new PollAnswer { PollId = "t", Text = "  " }));
    }
}

public class PollHubTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Poll_runs_end_to_end_with_live_results()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync("PC1");
        await using var s2 = await _factory.ConnectAsync();
        await s2.RegisterStudentAsync("PC2");
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        // Students can't start polls.
        await Assert.ThrowsAsync<HubException>(() => s1.Hub.InvokeAsync<Poll>(Protocol.ServerMethods.StartPoll, new Poll { Question = "x" }));

        var poll = await teacher.Hub.InvokeAsync<Poll>(Protocol.ServerMethods.StartPoll,
            new Poll { Question = "Pick one", Kind = PollKind.MultipleChoice, Options = ["A", "B", "C"] });
        Assert.False(string.IsNullOrEmpty(poll.Id));
        await s1.WaitForAsync(Protocol.ClientMethods.PollStarted);
        await s2.WaitForAsync(Protocol.ClientMethods.PollStarted);

        await s1.Hub.InvokeAsync(Protocol.ServerMethods.SubmitPollAnswer, new PollAnswer { PollId = poll.Id, Choice = 0 });
        await s2.Hub.InvokeAsync(Protocol.ServerMethods.SubmitPollAnswer, new PollAnswer { PollId = poll.Id, Choice = 2 });
        await s2.Hub.InvokeAsync(Protocol.ServerMethods.SubmitPollAnswer, new PollAnswer { PollId = poll.Id, Choice = 1 });  // changed mind
        await s2.Hub.InvokeAsync(Protocol.ServerMethods.SubmitPollAnswer, new PollAnswer { PollId = poll.Id, Choice = 9 });  // out of range: ignored

        var results = await WaitForResults(teacher, r => r.Answered == 2 && r.Counts[1] == 1);
        Assert.Equal([1, 1, 0], results.Counts);
        Assert.Equal(2, results.Asked);
        Assert.Contains(results.Responses, r => r.Student == "PC2" && r.Choice == 1);

        // A teacher can't vote; closing removes the card and freezes the results.
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.SubmitPollAnswer, new PollAnswer { PollId = poll.Id, Choice = 0 });
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.ClosePoll);
        await s1.WaitForAsync(Protocol.ClientMethods.PollClosed, a => (string)a[0]! == poll.Id);
        await WaitForResults(teacher, r => r.Closed);
        await s1.Hub.InvokeAsync(Protocol.ServerMethods.SubmitPollAnswer, new PollAnswer { PollId = poll.Id, Choice = 2 });
        var final = await WaitForResults(teacher, r => r.Closed);
        Assert.Equal([1, 1, 0], final.Counts);
    }

    [Fact]
    public async Task Late_joiners_get_the_open_poll_and_anonymous_hides_names()
    {
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        var poll = await teacher.Hub.InvokeAsync<Poll>(Protocol.ServerMethods.StartPoll,
            new Poll { Question = "One word?", Kind = PollKind.ShortText, Anonymous = true });

        await using var late = await _factory.ConnectAsync();
        await late.RegisterStudentAsync("LATE");
        Assert.Equal(poll.Id, ((Poll)(await late.WaitForAsync(Protocol.ClientMethods.PollStarted))[0]!).Id);

        await late.Hub.InvokeAsync(Protocol.ServerMethods.SubmitPollAnswer, new PollAnswer { PollId = poll.Id, Text = "photosynthesis" });
        var r = await WaitForResults(teacher, x => x.Answered == 1);
        var resp = Assert.Single(r.Responses);
        Assert.Equal("photosynthesis", resp.Text);
        Assert.Equal("", resp.Student);              // no names in an anonymous poll
        Assert.Equal("", resp.StudentConnectionId);

        // Last teacher leaves: the poll closes for the students.
        await teacher.Hub.StopAsync();
        await late.WaitForAsync(Protocol.ClientMethods.PollClosed);
    }

    private static async Task<PollResults> WaitForResults(Peer teacher, Func<PollResults, bool> match) =>
        (PollResults)(await teacher.WaitForAsync(Protocol.ClientMethods.PollResultsUpdated, a => match((PollResults)a[0]!)))[0]!;
}
