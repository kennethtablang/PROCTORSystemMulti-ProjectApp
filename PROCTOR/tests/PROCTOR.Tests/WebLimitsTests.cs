using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTOR.Tests;

/// <summary>Website whitelists / blacklists: per-student lists, and limits that outlast the lesson.</summary>
public class WebLimitsTests
{
    private static Task<ClassPolicy> SetClass(Peer teacher, ClassPolicy policy) =>
        teacher.Hub.InvokeAsync<ClassPolicy>(Protocol.ServerMethods.SetPolicy, policy);

    /// <summary>The policy pushed after the first <paramref name="seen"/> ones (the helper's inbox keeps everything).</summary>
    private static async Task<ClassPolicy> NextPolicy(Peer peer, int seen)
    {
        await peer.WaitForAsync(Protocol.ClientMethods.PolicyUpdated, _ => peer.All(Protocol.ClientMethods.PolicyUpdated).Count > seen);
        return (ClassPolicy)peer.All(Protocol.ClientMethods.PolicyUpdated)[seen][0]!;
    }

    private static int Seen(Peer peer) => peer.All(Protocol.ClientMethods.PolicyUpdated).Count;

    private static Task<int> SetOwn(Peer teacher, Peer student, WebLimits? limits) =>
        teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.SetStudentWebLimits,
            new StudentWebLimitsRequest { Students = [student.Id], Limits = limits });

    [Fact]
    public async Task A_student_can_get_its_own_list_and_go_back_to_the_class_list()
    {
        await using var factory = new ServerFactory();
        await using var a = await factory.ConnectAsync();
        await a.RegisterStudentAsync("PC-A");
        await using var b = await factory.ConnectAsync();
        await b.RegisterStudentAsync("PC-B");
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        // Class blacklist for everyone.
        await SetClass(teacher, new ClassPolicy { WebMode = WebLimitMode.BlockListed, WebHosts = ["youtube.com"] });
        await a.WaitForAsync(Protocol.ClientMethods.PolicyUpdated, p => ((ClassPolicy)p[0]!).WebMode == WebLimitMode.BlockListed);
        await b.WaitForAsync(Protocol.ClientMethods.PolicyUpdated, p => ((ClassPolicy)p[0]!).WebMode == WebLimitMode.BlockListed);

        // PC-A alone gets a whitelist; PC-B keeps the class blacklist (nothing new pushed to it).
        int seenA = Seen(a), seenB = Seen(b);
        Assert.Equal(1, await SetOwn(teacher, a, new WebLimits { WebMode = WebLimitMode.AllowOnlyListed, WebHosts = ["Docs.Google.com"] }));
        var own = await NextPolicy(a, seenA);
        Assert.Equal(WebLimitMode.AllowOnlyListed, own.WebMode);
        Assert.Equal(["docs.google.com"], own.WebHosts);
        await Task.Delay(500);
        Assert.Equal(seenB, Seen(b));

        // Teachers see which PC has its own list.
        var roster = await teacher.Hub.InvokeAsync<RosterSnapshot>(Protocol.ServerMethods.GetRoster);
        Assert.Equal(WebLimitMode.AllowOnlyListed, roster.Students.Single(s => s.MachineName == "PC-A").OwnWebLimits?.WebMode);
        Assert.Null(roster.Students.Single(s => s.MachineName == "PC-B").OwnWebLimits);

        // Changing the class list leaves PC-A on its own list.
        seenA = Seen(a);
        await SetClass(teacher, new ClassPolicy { WebMode = WebLimitMode.BlockListed, WebHosts = ["tiktok.com"] });
        var stillOwn = await NextPolicy(a, seenA);
        Assert.Equal(["docs.google.com"], stillOwn.WebHosts);

        // Back to the class list.
        seenA = Seen(a);
        await SetOwn(teacher, a, null);
        var back = await NextPolicy(a, seenA);
        Assert.Equal(WebLimitMode.BlockListed, back.WebMode);
        Assert.Equal(["tiktok.com"], back.WebHosts);
    }

    [Fact]
    public async Task Only_teachers_set_a_students_list_and_it_needs_a_student()
    {
        await using var factory = new ServerFactory();
        await using var a = await factory.ConnectAsync();
        await a.RegisterStudentAsync("PC-A");
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Assert.ThrowsAsync<HubException>(() => SetOwn(a, a, new WebLimits { WebMode = WebLimitMode.AllowOnlyListed }));
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.SetStudentWebLimits,
            new StudentWebLimitsRequest { Students = [], Limits = new WebLimits() }));
    }

    [Fact]
    public async Task With_strong_controls_reserved_website_limits_end_with_the_lesson()
    {
        // Open mode with the strong controls kept for code holders: a codeless teacher can't leave
        // the lab stuck on a list, nor give a PC its own.
        await using var factory = new ServerFactory(("PROCTOR:AllowStrongControlsInOpenMode", "false"));
        await using var a = await factory.ConnectAsync();
        await a.RegisterStudentAsync("PC-A");
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Assert.ThrowsAsync<HubException>(() => SetOwn(teacher, a, new WebLimits { WebMode = WebLimitMode.AllowOnlyListed }));
        await SetClass(teacher, new ClassPolicy { WebMode = WebLimitMode.AllowOnlyListed, WebHosts = ["wikipedia.org"] });
        await a.WaitForAsync(Protocol.ClientMethods.PolicyUpdated, p => ((ClassPolicy)p[0]!).WebMode == WebLimitMode.AllowOnlyListed);

        await teacher.Hub.StopAsync();
        await a.WaitForAsync(Protocol.ClientMethods.PolicyUpdated, p => ((ClassPolicy)p[0]!).IsEmpty);
    }

    [Fact]
    public async Task Website_limits_survive_a_server_restart_and_follow_the_pc()
    {
        var store = Path.Combine(Path.GetTempPath(), "ci-test-web-shared-" + Guid.NewGuid().ToString("N") + ".json");
        var cert = Path.Combine(Path.GetTempPath(), "ci-test-shared-" + Guid.NewGuid().ToString("N") + ".pfx");
        var agents = Path.Combine(Path.GetTempPath(), "ci-test-agents-shared-" + Guid.NewGuid().ToString("N") + ".json");
        (string, string?)[] settings = [("PROCTOR:WebLimitsPath", store), ("PROCTOR:CertificatePath", cert), ("PROCTOR:AgentStorePath", agents)];
        var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var machine = "lab-" + Guid.NewGuid().ToString("N");
        StudentInfo Info(string name) => new() { MachineId = machine, MachineName = name };

        try
        {
            await using (var first = new ServerFactory(settings))
            {
                await using var pc = await first.ConnectAsync();
                await pc.RegisterStudentAsync(Info("PC-A"), key);
                await using var teacher = await first.ConnectAsync();
                await teacher.RegisterTeacherAsync();
                await SetClass(teacher, new ClassPolicy { WebMode = WebLimitMode.BlockListed, WebHosts = ["youtube.com"] });
                await SetOwn(teacher, pc, new WebLimits { WebMode = WebLimitMode.AllowOnlyListed, WebHosts = ["wikipedia.org"] });
            }

            // New server process, no teacher connected: the PC still gets its own whitelist.
            await using var second = new ServerFactory(settings);
            await using var again = await second.ConnectAsync();
            await again.RegisterStudentAsync(Info("PC-A"), key);
            var policy = (ClassPolicy)(await again.WaitForAsync(Protocol.ClientMethods.PolicyUpdated))[0]!;
            Assert.Equal(WebLimitMode.AllowOnlyListed, policy.WebMode);
            Assert.Equal(["wikipedia.org"], policy.WebHosts);

            // ...and another PC gets the class blacklist.
            await using var other = await second.ConnectAsync();
            await other.RegisterStudentAsync("PC-B");
            var classPolicy = (ClassPolicy)(await other.WaitForAsync(Protocol.ClientMethods.PolicyUpdated))[0]!;
            Assert.Equal(["youtube.com"], classPolicy.WebHosts);
        }
        finally
        {
            foreach (var f in new[] { store, cert, agents }) try { File.Delete(f); } catch { }
        }
    }
}
