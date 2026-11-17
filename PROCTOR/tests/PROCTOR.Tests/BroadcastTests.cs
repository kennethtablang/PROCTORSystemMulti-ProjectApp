using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTOR.Tests;

/// <summary>Screen broadcast (FR-CTRL-02): authorisation, pair-gated signalling, late joiners, fail-safe.</summary>
public class BroadcastHubTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static Task<IReadOnlyList<string>> Start(Peer teacher, BroadcastMode mode = BroadcastMode.Window, params string[] targets) =>
        teacher.Hub.InvokeAsync<IReadOnlyList<string>>(Protocol.ServerMethods.StartBroadcast,
            new BroadcastRequest { Mode = mode, Targets = targets });

    [Fact]
    public async Task Broadcast_signalling_flows_only_within_the_broadcast()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync("PC1");
        await using var s2 = await _factory.ConnectAsync();
        await s2.RegisterStudentAsync("PC2");
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        await using var other = await _factory.ConnectAsync();
        await other.RegisterTeacherAsync();

        // Students can't broadcast.
        await Assert.ThrowsAsync<HubException>(() => Start(s1));

        // Targeted broadcast: only PC1.
        var targets = await Start(teacher, BroadcastMode.FullScreen, s1.Id);
        Assert.Equal([s1.Id], targets);
        var info = (BroadcastInfo)(await s1.WaitForAsync(Protocol.ClientMethods.BroadcastStarted))[0]!;
        Assert.Equal(teacher.Id, info.TeacherConnectionId);
        Assert.Equal(BroadcastMode.FullScreen, info.Mode);
        await s2.AssertNoneAsync(Protocol.ClientMethods.BroadcastStarted);

        // Offer teacher -> PC1 is relayed; to PC2 (not a target) and from another teacher it is not.
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.SendBroadcastOffer, new SdpMessage { TargetConnectionId = s1.Id, Sdp = "offer" });
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.SendBroadcastOffer, new SdpMessage { TargetConnectionId = s2.Id, Sdp = "evil" });
        await other.Hub.InvokeAsync(Protocol.ServerMethods.SendBroadcastOffer, new SdpMessage { TargetConnectionId = s1.Id, Sdp = "evil" });
        var offer = (SdpMessage)(await s1.WaitForAsync(Protocol.ClientMethods.ReceiveBroadcastOffer))[0]!;
        Assert.Equal(teacher.Id, offer.FromConnectionId);

        // Answer PC1 -> teacher relayed; PC2 -> teacher not.
        await s1.Hub.InvokeAsync(Protocol.ServerMethods.SendBroadcastAnswer, new SdpMessage { TargetConnectionId = teacher.Id, Sdp = "answer" });
        await s2.Hub.InvokeAsync(Protocol.ServerMethods.SendBroadcastAnswer, new SdpMessage { TargetConnectionId = teacher.Id, Sdp = "evil" });
        await s1.Hub.InvokeAsync(Protocol.ServerMethods.SendBroadcastIce, new IceCandidateMessage { TargetConnectionId = teacher.Id, Candidate = "c" });
        await teacher.WaitForAsync(Protocol.ClientMethods.ReceiveBroadcastIce);
        await Task.Delay(600);
        Assert.Equal(["offer"], s1.All(Protocol.ClientMethods.ReceiveBroadcastOffer).Select(a => ((SdpMessage)a[0]!).Sdp));
        Assert.Empty(s2.All(Protocol.ClientMethods.ReceiveBroadcastOffer));
        Assert.Equal(["answer"], teacher.All(Protocol.ClientMethods.ReceiveBroadcastAnswer).Select(a => ((SdpMessage)a[0]!).Sdp));

        // Stop: the viewer closes.
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.StopBroadcast);
        await s1.WaitForAsync(Protocol.ClientMethods.BroadcastStopped);
    }

    [Fact]
    public async Task Whole_class_broadcast_reaches_late_joiners_and_stops_if_the_teacher_leaves()
    {
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        Assert.Empty(await Start(teacher));   // no students yet

        await using var late = await _factory.ConnectAsync();
        await late.RegisterStudentAsync("LATE");
        await late.WaitForAsync(Protocol.ClientMethods.BroadcastStarted);
        Assert.Equal(late.Id, (string)(await teacher.WaitForAsync(Protocol.ClientMethods.BroadcastStudentJoined))[0]!);

        var teacherId = teacher.Id;
        await teacher.Hub.StopAsync();
        await late.WaitForAsync(Protocol.ClientMethods.BroadcastStopped, a => (string)a[0]! == teacherId);
    }

    [Fact]
    public async Task A_new_broadcast_replaces_the_old_one()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var t1 = await _factory.ConnectAsync();
        await t1.RegisterTeacherAsync();
        await using var t2 = await _factory.ConnectAsync();
        await t2.RegisterTeacherAsync();

        await Start(t1);
        await s1.WaitForAsync(Protocol.ClientMethods.BroadcastStarted, a => ((BroadcastInfo)a[0]!).TeacherConnectionId == t1.Id);
        await Start(t2);
        await s1.WaitForAsync(Protocol.ClientMethods.BroadcastStopped, a => (string)a[0]! == t1.Id);
        await s1.WaitForAsync(Protocol.ClientMethods.BroadcastStarted, a => ((BroadcastInfo)a[0]!).TeacherConnectionId == t2.Id);

        // The replaced teacher can no longer signal to the class.
        await t1.Hub.InvokeAsync(Protocol.ServerMethods.SendBroadcastOffer, new SdpMessage { TargetConnectionId = s1.Id, Sdp = "stale" });
        await s1.AssertNoneAsync(Protocol.ClientMethods.ReceiveBroadcastOffer);
    }
}
