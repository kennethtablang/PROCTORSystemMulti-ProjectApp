using ClassInsight.Server.Services;
using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClassInsight.Tests;

public class TimerStateTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Counts_down_and_expires()
    {
        var clock = new Clock();
        var t = new TimerState(clock);
        Assert.False(t.Snapshot().Running);
        t.Start("Worksheet", 600);
        clock.Now = clock.Now.AddSeconds(90);
        var s = t.Snapshot();
        Assert.True(s.Running);
        Assert.Equal(510, s.RemainingSeconds);
        Assert.Equal(600, s.TotalSeconds);
        clock.Now = clock.Now.AddSeconds(600);
        Assert.False(t.Snapshot().Running);   // expired
    }

    [Theory]
    [InlineData("  Finish\nthe worksheet  ", "Finish the worksheet")]
    [InlineData("   ", null)]
    public void Labels_are_one_clean_line(string input, string? expected) =>
        Assert.Equal(expected, TimerLimits.CleanLabel(input));
}

public class TimerHubTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Timer_reaches_students_late_joiners_and_stops_when_teachers_leave()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Assert.ThrowsAsync<HubException>(() => s1.Hub.InvokeAsync<LessonTimer>(Protocol.ServerMethods.StartTimer, new TimerRequest { Seconds = 60 }));
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync<LessonTimer>(Protocol.ServerMethods.StartTimer, new TimerRequest { Seconds = 1 }));

        await teacher.Hub.InvokeAsync<LessonTimer>(Protocol.ServerMethods.StartTimer, new TimerRequest { Seconds = 300, Label = "Quiz" });
        var t = (LessonTimer)(await s1.WaitForAsync(Protocol.ClientMethods.TimerUpdated, a => ((LessonTimer)a[0]!).Running))[0]!;
        Assert.Equal("Quiz", t.Label);
        Assert.InRange(t.RemainingSeconds, 295, 300);

        await using var late = await _factory.ConnectAsync();
        await late.RegisterStudentAsync("LATE");
        await late.WaitForAsync(Protocol.ClientMethods.TimerUpdated, a => ((LessonTimer)a[0]!).Running);

        await teacher.Hub.StopAsync();
        await late.WaitForAsync(Protocol.ClientMethods.TimerUpdated, a => !((LessonTimer)a[0]!).Running);
    }
}
