using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTOR.Tests;

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
    [InlineData("Word", "winword.exe")]
    [InlineData("  chrome  ", "chrome.exe")]
    [InlineData("notepad.exe", "notepad.exe")]
    [InlineData("code", "code")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData("\"C:\\Tools\\x.exe\"", @"C:\Tools\x.exe")]
    public void Programs_are_names_or_exe_paths(string input, string expected) =>
        Assert.Equal(expected, ClassroomLimits.CleanProgram(input));

    [Theory]
    [InlineData("notepad.exe & calc.exe")]
    [InlineData("cmd /c del *")]
    [InlineData("calc.exe | evil")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData(@"C:\Windows\..\x.exe")]
    [InlineData(@"C:\scripts\run.bat")]
    [InlineData("powershell -enc AAAA")]
    [InlineData("%COMSPEC%")]
    [InlineData("")]
    public void Programs_with_arguments_or_shell_syntax_are_refused(string input) =>
        Assert.Null(ClassroomLimits.CleanProgram(input));

    [Fact]
    public void Telemetry_from_a_student_is_bounded()
    {
        var now = DateTimeOffset.UtcNow;
        var flood = new TelemetrySnapshot
        {
            ForegroundWindowTitle = new string('x', 10_000),
            TopProcesses = Enumerable.Range(0, 1000).Select(i => new ProcessSample { Pid = i, Name = new string('p', 500) }).ToArray(),
            RemovableDrives = Enumerable.Range(0, 100).Select(_ => new RemovableDrive { Letter = "E:", Label = new string('l', 500) })
                .Append(new RemovableDrive { Letter = "not a drive" }).ToArray(),
            RecentBlockedSites = Enumerable.Range(0, 1000).Select(i => new BlockedSiteEvent { Host = $"site{i}.com", At = now })
                .Append(new BlockedSiteEvent { Host = "javascript:alert(1)", At = now })
                .Append(new BlockedSiteEvent { Host = "old.com", At = now.AddHours(-1) })
                .ToArray(),
        };

        var clean = TelemetryLimits.Clean(flood, now);

        Assert.Equal(TelemetryLimits.MaxTitleLength, clean.ForegroundWindowTitle!.Length);
        Assert.Equal(TelemetryLimits.MaxProcesses, clean.TopProcesses.Count);
        Assert.All(clean.TopProcesses, p => Assert.True(p.Name.Length <= TelemetryLimits.MaxNameLength));
        Assert.Equal(RemovableDrive.MaxReported, clean.RemovableDrives.Count);
        Assert.All(clean.RemovableDrives, d => Assert.True(d.Label!.Length <= TelemetryLimits.MaxLabelLength));
        Assert.Equal(BlockedSiteEvent.MaxReported, clean.RecentBlockedSites.Count);
        Assert.DoesNotContain(clean.RecentBlockedSites, b => b.Host.Contains("javascript") || b.Host == "old.com");
    }

    [Fact]
    public void Telemetry_cleaning_bounds_blocked_programs_idle_time_and_inventory()
    {
        var now = DateTimeOffset.UtcNow;
        var flood = new TelemetrySnapshot
        {
            RecentBlockedApps = Enumerable.Range(0, 50)
                .Select(i => new BlockedAppEvent { Name = (i == 0 ? "\u0007" : "Game" + i) + new string('x', 500), At = now })
                .Append(new BlockedAppEvent { Name = "old", At = now.AddHours(-1) })
                .Append(new BlockedAppEvent { Name = "   ", At = now })
                .ToArray(),
            IdleSeconds = int.MaxValue,
            Inventory = new PcInventory
            {
                CpuName = new string('c', 5000),
                Windows = "Windows\r\n11",
                CpuCores = -3,
                DiskTotalBytes = 100,
                DiskFreeBytes = 500,
                BootedAt = now.AddYears(5),
                AgentVersion = new string('9', 200),
            },
        };

        var clean = TelemetryLimits.Clean(flood, now);

        Assert.Equal(BlockedAppEvent.MaxReported, clean.RecentBlockedApps.Count);
        Assert.All(clean.RecentBlockedApps, a => Assert.True(a.Name.Length is > 0 and <= TelemetryLimits.MaxNameLength));
        Assert.DoesNotContain(clean.RecentBlockedApps, a => a.Name == "old" || a.Name.Any(char.IsControl));
        Assert.Equal(TelemetryLimits.MaxIdleSeconds, clean.IdleSeconds);
        Assert.Null(TelemetryLimits.Clean(new TelemetrySnapshot(), now).IdleSeconds);

        var inv = clean.Inventory!;
        Assert.Equal(TelemetryLimits.MaxNameLength, inv.CpuName!.Length);
        Assert.Equal("Windows11", inv.Windows);
        Assert.Equal(0, inv.CpuCores);
        Assert.Equal(100, inv.DiskFreeBytes);   // never more free space than the disk holds
        Assert.Null(inv.BootedAt);              // a start time in the future is nonsense
        Assert.Equal(32, inv.AgentVersion!.Length);
    }

    [Fact]
    public void Policy_cleaning_keeps_flash_drive_block_and_exam()
    {
        var clean = ClassroomLimits.Clean(new ClassPolicy { BlockUsbStorage = true, ExamMode = true, ExamTitle = "Quiz\n3" });
        Assert.True(clean.BlockUsbStorage);
        Assert.True(clean.ExamMode);
        Assert.Equal("Quiz 3", clean.ExamTitle);
        Assert.False(clean.IsEmpty);
        Assert.True(clean.HasLessonLimits);
        Assert.Null(ClassroomLimits.Clean(new ClassPolicy { ExamTitle = "ignored without exam" }).ExamTitle);
    }

    [Theory]
    [InlineData("#1e4fbf", "#1E4FBF")]
    [InlineData("1E4FBF", "#1E4FBF")]
    [InlineData("#abc", "#AABBCC")]
    [InlineData(" #000000 ", "#000000")]
    [InlineData("red", null)]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("url(javascript:x)", null)]
    [InlineData("", null)]
    public void Lock_colours_are_hex_only(string input, string? expected) =>
        Assert.Equal(expected, ClassroomLimits.CleanColor(input));

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#14121E", true)]
    [InlineData("#1E4FBF", true)]
    [InlineData("#FFFFFF", false)]
    [InlineData("#F2C230", false)]
    public void Lock_text_stays_readable_on_its_background(string background, bool lightText) =>
        Assert.Equal(lightText, ClassroomLimits.PrefersLightText(background));

    [Fact]
    public void Lock_titles_are_one_short_line()
    {
        Assert.Equal("Look up", ClassroomLimits.CleanLockTitle("Look\nup"));
        Assert.Equal(ClassroomLimits.MaxLockTitleLength, ClassroomLimits.CleanLockTitle(new string('x', 300))!.Length);
        Assert.Null(ClassroomLimits.CleanLockTitle("   "));
    }

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
    [InlineData("PROCTOR.Agent", null)]  // protected
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
public class ClosedModeControlTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new(
        ("PROCTOR:AllowOpenTeacherMode", "false"),
        ("PROCTOR:TeacherCode", "ABCD-EFGH-JKMNP"));

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task With_a_join_code_the_strong_controls_are_available()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync("ABCD-EFGH-JKMNP");

        var applied = await teacher.Hub.InvokeAsync<ClassPolicy>(Protocol.ServerMethods.SetPolicy,
            new ClassPolicy { BlockInternet = true, BlockedApps = ["steam", "explorer"] });
        Assert.True(applied.BlockInternet);
        Assert.Equal(["steam"], applied.BlockedApps);   // explorer is protected
        Assert.Equal(1, await teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.SendCommand, new ClassroomCommand { Kind = CommandKind.Restart }));
    }
}

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
        // Strong controls reserved for code holders (not the default).
        await using var factory = new ServerFactory(("PROCTOR:AllowStrongControlsInOpenMode", "false"));
        await using var s1 = await factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        await Assert.ThrowsAsync<HubException>(() => Send(teacher, new ClassroomCommand { Kind = CommandKind.Message, Text = "  " }));
        await Assert.ThrowsAsync<HubException>(() => Send(teacher, new ClassroomCommand { Kind = CommandKind.OpenUrl, Url = "file:///C:/x.exe" }));
        // Open mode with strong controls reserved: refused, pointing at the join code.
        var ex = await Assert.ThrowsAsync<HubException>(() => Send(teacher, new ClassroomCommand { Kind = CommandKind.ShutDown }));
        Assert.Contains("join code", ex.Message);
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync<ClassPolicy>(Protocol.ServerMethods.SetPolicy,
            new ClassPolicy { BlockInternet = true }));
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
    public async Task Policy_is_cleaned_pushed_to_late_joiners_and_partly_lifted_when_no_teacher_remains()
    {
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        var applied = await teacher.Hub.InvokeAsync<ClassPolicy>(Protocol.ServerMethods.SetPolicy, new ClassPolicy
        {
            WebMode = WebLimitMode.BlockListed,
            WebHosts = ["YouTube.com"],
            MuteSound = true,
        });
        Assert.Equal(["youtube.com"], applied.WebHosts);
        Assert.True(applied.MuteSound);

        // A student joining later gets the limits on registration.
        await using var late = await _factory.ConnectAsync();
        await late.RegisterStudentAsync("LATE");
        var pushed = (ClassPolicy)(await late.WaitForAsync(Protocol.ClientMethods.PolicyUpdated))[0]!;
        Assert.Equal(["youtube.com"], pushed.WebHosts);

        // Last teacher leaves: sound (and program / internet) limits are lifted for everyone; the
        // website blacklist stays until a teacher changes it.
        await teacher.Hub.StopAsync();
        var after = (ClassPolicy)(await late.WaitForAsync(Protocol.ClientMethods.PolicyUpdated, a => !((ClassPolicy)a[0]!).MuteSound))[0]!;
        Assert.Equal(WebLimitMode.BlockListed, after.WebMode);
        Assert.Equal(["youtube.com"], after.WebHosts);
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
