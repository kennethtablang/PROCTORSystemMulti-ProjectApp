using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTOR.Tests;

/// <summary>Showing one student's screen to the class: the shown student isn't a viewer and is always told.</summary>
public class ShowStudentTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static Task<IReadOnlyList<string>> Show(Peer teacher, string? source) =>
        teacher.Hub.InvokeAsync<IReadOnlyList<string>>(Protocol.ServerMethods.StartBroadcast,
            new BroadcastRequest { Mode = BroadcastMode.Window, SourceStudentConnectionId = source });

    [Fact]
    public async Task Shown_student_is_told_excluded_and_ends_the_show_by_leaving()
    {
        await using var shown = await _factory.ConnectAsync();
        await shown.RegisterStudentAsync("LAB-07");
        var viewer = await _factory.ConnectAsync();
        await viewer.RegisterStudentAsync("LAB-08");
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Assert.ThrowsAsync<HubException>(() => Show(teacher, "not-a-student"));
        await Assert.ThrowsAsync<HubException>(() => Show(teacher, teacher.Id));

        var targets = await Show(teacher, shown.Id);
        Assert.Equal([viewer.Id], targets);
        var info = (BroadcastInfo)(await viewer.WaitForAsync(Protocol.ClientMethods.BroadcastStarted))[0]!;
        Assert.Equal("LAB-07", info.ShowingStudentName);
        Assert.True((bool)(await shown.WaitForAsync(Protocol.ClientMethods.ScreenShownToClass))[0]!);
        await shown.AssertNoneAsync(Protocol.ClientMethods.BroadcastStarted);

        // A late joiner sees whose screen it is.
        await using var late = await _factory.ConnectAsync();
        await late.RegisterStudentAsync("LAB-09");
        var lateInfo = (BroadcastInfo)(await late.WaitForAsync(Protocol.ClientMethods.BroadcastStarted))[0]!;
        Assert.Equal("LAB-07", lateInfo.ShowingStudentName);

        // Stopping tells the shown student too.
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.StopBroadcast);
        await viewer.WaitForAsync(Protocol.ClientMethods.BroadcastStopped);
        await shown.WaitForAsync(Protocol.ClientMethods.ScreenShownToClass, a => !(bool)a[0]!);

        // Start again; this time the shown student leaves, which ends the show for everyone.
        await Show(teacher, shown.Id);
        await late.WaitForAsync(Protocol.ClientMethods.BroadcastStarted);
        var stopsBefore = late.All(Protocol.ClientMethods.BroadcastStopped).Count;
        await shown.Hub.StopAsync();
        for (var i = 0; i < 50 && late.All(Protocol.ClientMethods.BroadcastStopped).Count == stopsBefore; i++) await Task.Delay(100);
        Assert.True(late.All(Protocol.ClientMethods.BroadcastStopped).Count > stopsBefore);
        await viewer.DisposeAsync();
    }

    [Fact]
    public async Task Teachers_own_screen_still_reaches_everyone()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync("PC1");
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        Assert.Equal([s1.Id], await Show(teacher, null));
        var info = (BroadcastInfo)(await s1.WaitForAsync(Protocol.ClientMethods.BroadcastStarted))[0]!;
        Assert.Null(info.ShowingStudentName);
        await s1.AssertNoneAsync(Protocol.ClientMethods.ScreenShownToClass);
    }
}
