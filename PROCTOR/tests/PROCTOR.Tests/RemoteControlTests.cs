using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTOR.Tests;

public class RemoteControlTests
{
    private static RemoteInputBatch Click(string student) => new()
    {
        StudentConnectionId = student,
        Events =
        [
            new RemoteInputEvent { Kind = RemoteInputKind.MouseMove, X = 0.5, Y = 0.5 },
            new RemoteInputEvent { Kind = RemoteInputKind.MouseDown, X = 0.5, Y = 0.5 },
            new RemoteInputEvent { Kind = RemoteInputKind.MouseUp, X = 0.5, Y = 0.5 },
            new RemoteInputEvent { Kind = RemoteInputKind.KeyDown, VirtualKey = 0x41 },
            new RemoteInputEvent { Kind = RemoteInputKind.KeyUp, VirtualKey = 0x41 },
        ],
    };

    [Theory]
    [InlineData(RemoteInputKind.MouseMove, 1.5, 0.5, 0, 0)]
    [InlineData(RemoteInputKind.MouseMove, 0.5, -0.1, 0, 0)]
    [InlineData(RemoteInputKind.KeyDown, 0.5, 0.5, 0, 0)]
    [InlineData(RemoteInputKind.KeyDown, 0.5, 0.5, 300, 0)]
    [InlineData(RemoteInputKind.Wheel, 0.5, 0.5, 0, 99999)]
    public void Bad_events_are_rejected(RemoteInputKind kind, double x, double y, int vk, int delta) =>
        Assert.False(RemoteControlLimits.IsValid(new RemoteInputEvent { Kind = kind, X = x, Y = y, VirtualKey = vk, Delta = delta }));

    [Fact]
    public async Task Open_mode_can_keep_remote_control_for_teachers_with_a_code()
    {
        // Default: every control works in open mode. IT can reserve the strong ones for code holders.
        await using var factory = new ServerFactory(("PROCTOR:AllowStrongControlsInOpenMode", "false"));
        await using var student = await factory.ConnectAsync();
        await student.RegisterStudentAsync();
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync(Protocol.ServerMethods.StartRemoteControl, student.Id));
        await student.AssertNoneAsync(Protocol.ClientMethods.RemoteControlChanged);
    }

    [Fact]
    public async Task Open_mode_allows_remote_control_by_default()
    {
        await using var factory = new ServerFactory();
        await using var student = await factory.ConnectAsync();
        await student.RegisterStudentAsync();
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.StartRemoteControl, student.Id);
        await student.WaitForAsync(Protocol.ClientMethods.RemoteControlChanged);
    }

    [Fact]
    public async Task Only_the_controlling_teacher_reaches_the_student_and_leaving_releases_it()
    {
        await using var factory = new ServerFactory(("PROCTOR:AllowStrongControlsInOpenMode", "true"));
        await using var student = await factory.ConnectAsync();
        await student.RegisterStudentAsync("LAB-03");
        await using var other = await factory.ConnectAsync();
        await other.RegisterStudentAsync("LAB-04");
        var t1 = await factory.ConnectAsync();
        await t1.RegisterTeacherAsync();
        await using var t2 = await factory.ConnectAsync();
        await t2.RegisterTeacherAsync();

        // Students can't take control; nobody can send input without control.
        await Assert.ThrowsAsync<HubException>(() => other.Hub.InvokeAsync(Protocol.ServerMethods.StartRemoteControl, student.Id));
        await Assert.ThrowsAsync<HubException>(() => t1.Hub.InvokeAsync(Protocol.ServerMethods.SendRemoteInput, Click(student.Id)));

        await t1.Hub.InvokeAsync(Protocol.ServerMethods.StartRemoteControl, student.Id);
        var info = (RemoteControlInfo)(await student.WaitForAsync(Protocol.ClientMethods.RemoteControlChanged))[0]!;
        Assert.True(info.Active);
        Assert.Equal(t1.Id, info.TeacherConnectionId);

        await t1.Hub.InvokeAsync(Protocol.ServerMethods.SendRemoteInput, Click(student.Id));
        var batch = (RemoteInputBatch)(await student.WaitForAsync(Protocol.ClientMethods.RemoteInputReceived))[0]!;
        Assert.Equal(5, batch.Events.Count);
        await other.AssertNoneAsync(Protocol.ClientMethods.RemoteInputReceived);

        // Another teacher can't inject; invalid input is refused.
        await Assert.ThrowsAsync<HubException>(() => t2.Hub.InvokeAsync(Protocol.ServerMethods.SendRemoteInput, Click(student.Id)));
        await Assert.ThrowsAsync<HubException>(() => t1.Hub.InvokeAsync(Protocol.ServerMethods.SendRemoteInput, new RemoteInputBatch
        {
            StudentConnectionId = student.Id,
            Events = [new RemoteInputEvent { Kind = RemoteInputKind.MouseMove, X = 7 }],
        }));

        // t2 takes over: t1 is told and loses the right to send.
        await t2.Hub.InvokeAsync(Protocol.ServerMethods.StartRemoteControl, student.Id);
        Assert.Equal(student.Id, (string)(await t1.WaitForAsync(Protocol.ClientMethods.RemoteControlEnded))[0]!);
        await Assert.ThrowsAsync<HubException>(() => t1.Hub.InvokeAsync(Protocol.ServerMethods.SendRemoteInput, Click(student.Id)));

        // The controlling teacher disconnecting releases the PC.
        var t2Id = t2.Id;
        await t2.Hub.StopAsync();
        await student.WaitForAsync(Protocol.ClientMethods.RemoteControlChanged,
            a => a[0] is RemoteControlInfo { Active: false } i && i.TeacherConnectionId == t2Id);
        await t1.DisposeAsync();
    }

    [Fact]
    public async Task Student_leaving_ends_control_and_pcs_can_opt_out()
    {
        await using var factory = new ServerFactory(("PROCTOR:AllowStrongControlsInOpenMode", "true"));
        await using var student = await factory.ConnectAsync();
        await student.RegisterStudentAsync();
        await using var optedOut = await factory.ConnectAsync();
        await optedOut.RegisterStudentAsync(new StudentInfo { MachineId = "x-" + Guid.NewGuid().ToString("N"), MachineName = "KIOSK", RemoteControlAllowed = false });
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync(Protocol.ServerMethods.StartRemoteControl, optedOut.Id));

        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.StartRemoteControl, student.Id);
        var studentId = student.Id;
        await student.Hub.StopAsync();
        Assert.Equal(studentId, (string)(await teacher.WaitForAsync(Protocol.ClientMethods.RemoteControlEnded))[0]!);
    }
}
