using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace ClassInsight.Tests;

/// <summary>Validation rules for classroom control (P5): URLs, sites, apps, policies.</summary>
public class ClassroomLimitsTests
{
    [Theory]
    [InlineData("docs.google.com", "https://docs.google.com/")]
    [InlineData("http://example.org/a?b=1", "http://example.org/a?b=1")]
    public void Urls_are_normalised(string input, string expected)
    {
        Assert.True(ClassroomLimits.TryCleanUrl(input, out var clean));
        Assert.Equal(expected, clean);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("calc.exe\" --evil")]
    [InlineData("")]
    public void Dangerous_or_bogus_urls_are_refused(string input) =>
        Assert.False(ClassroomLimits.TryCleanUrl(input, out _));

    [Theory]
    [InlineData("YouTube.com", "youtube.com")]
    [InlineData("https://www.tiktok.com/@x", "tiktok.com")]
    [InlineData("*.example.org", "example.org")]
    [InlineData("not a host", null)]
    public void Hosts_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, ClassroomLimits.CleanHost(input));

    [Fact]
    public void Host_matching_covers_subdomains_but_not_lookalikes()
    {
        string[] listed = ["youtube.com"];
        Assert.True(ClassroomLimits.HostMatches("m.youtube.com", listed));
        Assert.True(ClassroomLimits.HostMatches("youtube.com", listed));
        Assert.False(ClassroomLimits.HostMatches("notyoutube.com", listed));
    }

    [Fact]
    public void Site_allowed_follows_the_mode()
    {
        var block = new ClassPolicy { WebMode = WebLimitMode.BlockListed, WebHosts = ["youtube.com"] };
        var allow = new ClassPolicy { WebMode = WebLimitMode.AllowOnlyListed, WebHosts = ["docs.google.com"] };
        Assert.False(ClassroomLimits.IsSiteAllowed("www.youtube.com", block));
        Assert.True(ClassroomLimits.IsSiteAllowed("docs.google.com", block));
        Assert.True(ClassroomLimits.IsSiteAllowed("docs.google.com", allow));
        Assert.False(ClassroomLimits.IsSiteAllowed("youtube.com", allow));
        Assert.True(ClassroomLimits.IsSiteAllowed(null, allow));   // new tab / start page
    }

    [Theory]
    [InlineData("Steam.exe", "steam")]
    [InlineData(@"C:\Games\discord.exe", "discord")]
    [InlineData("explorer", null)]            // protected
    [InlineData("ClassInsight.Agent", null)]  // protected
    [InlineData("winlogon.exe", null)]        // protected
    public void App_names_are_normalised_and_protected(string input, string? expected) =>
        Assert.Equal(expected, ClassroomLimits.CleanAppName(input));

    [Fact]
    public void Policy_cleaning_dedupes_drops_junk_and_protected()
    {
        var clean = ClassroomLimits.Clean(new ClassPolicy
        {
            WebMode = WebLimitMode.BlockListed,
            WebHosts = ["YouTube.com", "youtube.com", "not a host"],
            BlockedApps = ["steam", "STEAM.exe", "explorer"],
            MuteSound = true,
        });
        Assert.Equal(["youtube.com"], clean.WebHosts);
        Assert.Equal(["steam"], clean.BlockedApps);
        Assert.True(clean.MuteSound);
        Assert.False(clean.IsEmpty);
        Assert.True(new ClassPolicy().IsEmpty);
    }

    [Fact]
    public void Text_is_cleaned_and_capped()
    {
        Assert.Null(ClassroomLimits.CleanText("   "));
        Assert.Equal("hi\nthere", ClassroomLimits.CleanText(" hi\nthere\u0007 "));
        Assert.Equal(ClassroomLimits.MaxTextLength, ClassroomLimits.CleanText(new string('x', 2000))!.Length);
    }
}

/// <summary>Classroom-control hub flows (P5): commands, locks, limits, raised hands, fail-safes.</summary>
public class ClassroomControlHubTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private Task<int> Send(Peer teacher, ClassroomCommand cmd) =>
        teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.SendCommand, cmd);

    [Fact]
    public async Task Only_teachers_can_command_and_commands_reach_only_targets()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync("PC1");
        await using var s2 = await _factory.ConnectAsync();
        await s2.RegisterStudentAsync("PC2");
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        // A student can't command another student.
        await Assert.ThrowsAsync<HubException>(() =>
            s1.Hub.InvokeAsync<int>(Protocol.ServerMethods.SendCommand, new ClassroomCommand { Kind = CommandKind.Lock }));

        Assert.Equal(1, await Send(teacher, new ClassroomCommand { Kind = CommandKind.Message, Text = "Hello PC1", Targets = [s1.Id, "ghost"] }));
        var got = await s1.WaitForAsync(Protocol.ClientMethods.CommandReceived);
        Assert.Equal("Hello PC1", ((ClassroomCommand)got[1]!).Text);
        await s2.AssertNoneAsync(Protocol.ClientMethods.CommandReceived);

        // Empty targets = whole class.
        Assert.Equal(2, await Send(teacher, new ClassroomCommand { Kind = CommandKind.Message, Text = "All" }));
    }

    [Fact]
    public async Task Invalid_commands_are_refused()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Assert.ThrowsAsync<HubException>(() => Send(teacher, new ClassroomCommand { Kind = CommandKind.Message, Text = "  " }));
        await Assert.ThrowsAsync<HubException>(() => Send(teacher, new ClassroomCommand { Kind = CommandKind.OpenUrl, Url = "file:///C:/x.exe" }));
        // Open mode: power control is off unless IT allows it.
        var ex = await Assert.ThrowsAsync<HubException>(() => Send(teacher, new ClassroomCommand { Kind = CommandKind.ShutDown }));
        Assert.Contains("join code", ex.Message);
        await s1.AssertNoneAsync(Protocol.ClientMethods.CommandReceived);

        Assert.Equal(1, await Send(teacher, new ClassroomCommand { Kind = CommandKind.OpenUrl, Url = "docs.google.com" }));
        var got = (ClassroomCommand)(await s1.WaitForAsync(Protocol.ClientMethods.CommandReceived))[1]!;
        Assert.Equal("https://docs.google.com/", got.Url);
    }

    [Fact]
    public async Task Lock_state_is_tracked_and_released_when_the_teacher_leaves()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var observer = await _factory.ConnectAsync();
        await observer.RegisterTeacherAsync();
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Send(teacher, new ClassroomCommand { Kind = CommandKind.Lock });
        await observer.WaitForAsync(Protocol.ClientMethods.StudentLockChanged, a => (bool)a[1]! );
        var roster = await observer.Hub.InvokeAsync<RosterSnapshot>(Protocol.ServerMethods.GetRoster);
        Assert.True(Assert.Single(roster.Students).IsLocked);

        // The locking teacher's app crashes / disconnects: the student is unlocked automatically.
        await teacher.Hub.StopAsync();
        await s1.WaitForAsync(Protocol.ClientMethods.CommandReceived, a => ((ClassroomCommand)a[1]!).Kind == CommandKind.Unlock);
        await observer.WaitForAsync(Protocol.ClientMethods.StudentLockChanged, a => !(bool)a[1]!);
    }

    [Fact]
    public async Task Policy_is_cleaned_pushed_to_late_joiners_and_lifted_when_no_teacher_remains()
    {
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        var applied = await teacher.Hub.InvokeAsync<ClassPolicy>(Protocol.ServerMethods.SetPolicy, new ClassPolicy
        {
            WebMode = WebLimitMode.BlockListed,
            WebHosts = ["YouTube.com"],
            BlockedApps = ["explorer", "steam"],
        });
        Assert.Equal(["youtube.com"], applied.WebHosts);
        Assert.Equal(["steam"], applied.BlockedApps);   // explorer is protected

        // A student joining later gets the limits on registration.
        await using var late = await _factory.ConnectAsync();
        await late.RegisterStudentAsync("LATE");
        var pushed = (ClassPolicy)(await late.WaitForAsync(Protocol.ClientMethods.PolicyUpdated))[0]!;
        Assert.Equal(["youtube.com"], pushed.WebHosts);

        // Last teacher leaves: limits are lifted for everyone.
        await teacher.Hub.StopAsync();
        await late.WaitForAsync(Protocol.ClientMethods.PolicyUpdated, a => ((ClassPolicy)a[0]!).IsEmpty);
    }

    [Fact]
    public async Task Raised_hands_reach_teachers_and_are_acknowledged()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await s1.Hub.InvokeAsync(Protocol.ServerMethods.RaiseHand, true);
        await teacher.WaitForAsync(Protocol.ClientMethods.StudentHandChanged, a => (string)a[0]! == s1.Id && (bool)a[1]!);
        var roster = await teacher.Hub.InvokeAsync<RosterSnapshot>(Protocol.ServerMethods.GetRoster);
        Assert.True(Assert.Single(roster.Students).HandRaised);

        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.LowerHand, s1.Id);
        await s1.WaitForAsync(Protocol.ClientMethods.HandLowered);
        await teacher.WaitForAsync(Protocol.ClientMethods.StudentHandChanged, a => !(bool)a[1]!);

        // A teacher can't raise a hand, and a student can't lower someone else's.
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.RaiseHand, true);
        await s1.Hub.InvokeAsync(Protocol.ServerMethods.LowerHand, s1.Id);
        roster = await teacher.Hub.InvokeAsync<RosterSnapshot>(Protocol.ServerMethods.GetRoster);
        Assert.False(Assert.Single(roster.Students).HandRaised);
    }
}
