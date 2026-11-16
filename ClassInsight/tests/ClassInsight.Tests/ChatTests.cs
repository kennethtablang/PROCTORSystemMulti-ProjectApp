using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClassInsight.Tests;

public class ChatTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static Task Say(Peer from, string text, string? to = null) =>
        from.Hub.InvokeAsync(Protocol.ServerMethods.SendChat, new ChatSend { ToConnectionId = to, Text = text });

    [Fact]
    public async Task Conversations_reach_only_the_right_people()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync("LAB-01");
        await using var s2 = await _factory.ConnectAsync();
        await s2.RegisterStudentAsync("LAB-02");
        await using var t1 = await _factory.ConnectAsync();
        await t1.RegisterTeacherAsync();
        await using var t2 = await _factory.ConnectAsync();
        await t2.RegisterTeacherAsync();

        // Teacher → LAB-01: LAB-01 and both teachers see it; LAB-02 doesn't.
        await Say(t1, "How's it going?", s1.Id);
        var m = (ChatMessage)(await s1.WaitForAsync(Protocol.ClientMethods.ChatReceived))[0]!;
        Assert.True(m.FromTeacher);
        Assert.Equal(s1.Id, m.StudentConnectionId);
        await t2.WaitForAsync(Protocol.ClientMethods.ChatReceived);
        await s2.AssertNoneAsync(Protocol.ClientMethods.ChatReceived);

        // Student → teachers (a "to" a student tries to set is ignored): the other student never sees it.
        await Say(s1, "Stuck on question 3\u0007", s2.Id);
        var reply = (ChatMessage)(await t1.WaitForAsync(Protocol.ClientMethods.ChatReceived, a => !((ChatMessage)a[0]!).FromTeacher))[0]!;
        Assert.Equal("Stuck on question 3", reply.Text);
        Assert.Equal("LAB-01", reply.FromName);
        Assert.Equal(s1.Id, reply.StudentConnectionId);
        await s2.AssertNoneAsync(Protocol.ClientMethods.ChatReceived);

        // Bad requests.
        await Assert.ThrowsAsync<HubException>(() => Say(t1, "   ", s1.Id));
        await Assert.ThrowsAsync<HubException>(() => Say(t1, "hi", t2.Id));
        await using var stranger = await _factory.ConnectAsync();
        await Assert.ThrowsAsync<HubException>(() => Say(stranger, "hi"));
    }

    [Fact]
    public async Task Students_cannot_flood_the_teachers()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var t1 = await _factory.ConnectAsync();
        await t1.RegisterTeacherAsync();
        for (var i = 0; i < ChatLimits.StudentBurst; i++) await Say(s1, $"msg {i}");
        await Assert.ThrowsAsync<HubException>(() => Say(s1, "one too many"));
    }
}
