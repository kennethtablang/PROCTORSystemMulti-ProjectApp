using PROCTOR.Server.Services;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTOR.Tests;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("3c-52-82-1a-2b-3c", "3C-52-82-1A-2B-3C")]
    [InlineData("3C:52:82:1A:2B:3C", "3C-52-82-1A-2B-3C")]
    [InlineData("3C52821A2B3C", "3C-52-82-1A-2B-3C")]
    [InlineData("FF-FF-FF-FF-FF-FF", null)]   // broadcast
    [InlineData("01-00-5E-00-00-01", null)]   // multicast
    [InlineData("00-00-00-00-00-00", null)]
    [InlineData("3C-52-82-1A-2B", null)]
    [InlineData("ZZ-52-82-1A-2B-3C", null)]
    [InlineData(null, null)]
    public void Macs_are_validated_and_normalised(string? input, string? expected) =>
        Assert.Equal(expected, WakeOnLan.Normalize(input));

    [Fact]
    public void Magic_packet_is_six_FF_then_sixteen_copies_of_the_mac()
    {
        WakeOnLan.TryParseMac("3C-52-82-1A-2B-3C", out var mac);
        var p = WakeOnLan.MagicPacket(mac);
        Assert.Equal(102, p.Length);
        Assert.All(p[..6], b => Assert.Equal(0xFF, b));
        for (var r = 0; r < 16; r++) Assert.Equal(mac, p[(6 + r * 6)..(12 + r * 6)]);
    }

    [Fact]
    public void Sends_are_rate_limited()
    {
        var clock = new ManualClock();
        var w = new WakeSender(clock);
        Assert.True(w.TryReserve());
        Assert.False(w.TryReserve());
        clock.Now += WakeSender.MinInterval;
        Assert.True(w.TryReserve());
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public class WakeHubTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Teachers_see_clean_macs_and_only_teachers_can_wake()
    {
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        await using var student = await _factory.ConnectAsync();
        await student.RegisterStudentAsync(new StudentInfo
        {
            MachineId = "m1-" + Guid.NewGuid().ToString("N"),
            MachineName = "LAB-01",
            MacAddresses = ["02:00:00:aa:bb:01", "junk", "FF-FF-FF-FF-FF-FF", "02-00-00-AA-BB-01", "02-00-00-AA-BB-02",
                            "02-00-00-AA-BB-03", "02-00-00-AA-BB-04", "02-00-00-AA-BB-05"],
        });
        var snap = (StudentSnapshot)(await teacher.WaitForAsync(Protocol.ClientMethods.StudentJoined))[0]!;
        Assert.Equal(["02-00-00-AA-BB-01", "02-00-00-AA-BB-02", "02-00-00-AA-BB-03", "02-00-00-AA-BB-04"], snap.MacAddresses);

        await Assert.ThrowsAsync<HubException>(() =>
            student.Hub.InvokeAsync<int>(Protocol.ServerMethods.WakeComputers, new WakeRequest { MacAddresses = ["02-00-00-AA-BB-01"] }));
        await Assert.ThrowsAsync<HubException>(() =>
            teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.WakeComputers, new WakeRequest { MacAddresses = ["nope"] }));

        // A locally administered test MAC (02-…) — no real machine answers to it.
        var sent = await teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.WakeComputers,
            new WakeRequest { MacAddresses = ["02-00-00-AA-BB-01", "02:00:00:aa:bb:01"] });
        Assert.Equal(1, sent);

        // Straight away again: rate-limited.
        await Assert.ThrowsAsync<HubException>(() =>
            teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.WakeComputers, new WakeRequest { MacAddresses = ["02-00-00-AA-BB-02"] }));
    }
}
