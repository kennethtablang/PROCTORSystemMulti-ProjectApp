using System.Security.Cryptography;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace PROCTOR.Tests;

public class FileLimitsTests
{
    [Theory]
    [InlineData("report.docx", "report.docx")]
    [InlineData(@"..\..\Windows\System32\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("a<b>c:d|e?.txt", "a_b_c_d_e_.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("notes. . .", "notes")]
    [InlineData("...", null)]
    [InlineData("   ", null)]
    public void File_names_are_made_safe(string input, string? expected) =>
        Assert.Equal(expected, FileLimits.CleanFileName(input));

    [Fact]
    public void Executables_are_recognised() =>
        Assert.True(FileLimits.IsExecutable("setup.EXE") && FileLimits.IsExecutable("x.ps1") && !FileLimits.IsExecutable("essay.pdf"));

    [Fact]
    public void Assembler_checks_order_size_and_hash()
    {
        var bytes = RandomNumberGenerator.GetBytes(FileLimits.ChunkSize + 100);
        var sha = FileLimits.Hex(SHA256.HashData(bytes));
        var offer = new FileOffer { TransferId = "t", FileName = "a.bin", Size = bytes.Length };
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        using (var a = new FileAssembler(offer, temp))
        {
            Assert.False(a.Append(new FileChunk { Index = 1, Data = bytes[..10] }));   // out of order
            Assert.True(a.Append(new FileChunk { Index = 0, Data = bytes[..FileLimits.ChunkSize] }));
            Assert.Null(a.Finish(sha));                                                  // incomplete
        }
        using (var a = new FileAssembler(offer, temp))
        {
            a.Append(new FileChunk { Index = 0, Data = bytes[..FileLimits.ChunkSize] });
            a.Append(new FileChunk { Index = 1, Data = bytes[FileLimits.ChunkSize..] });
            Assert.False(a.Append(new FileChunk { Index = 2, Data = [1] }));             // beyond the announced size
            Assert.Null(a.Finish(new string('0', 64)));                                  // wrong hash
        }
        using (var a = new FileAssembler(offer, temp))
        {
            a.Append(new FileChunk { Index = 0, Data = bytes[..FileLimits.ChunkSize] });
            a.Append(new FileChunk { Index = 1, Data = bytes[FileLimits.ChunkSize..] });
            using var ok = a.Finish(sha);
            Assert.NotNull(ok);
            var copy = new MemoryStream();
            ok!.CopyTo(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        Assert.False(File.Exists(temp));   // temp file is removed
    }
}

public class FileTransferHubTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static Task<FileOffer> Send(Peer from, byte[] data, FileOffer offer) =>
        FileSender.SendAsync(
            o => from.Hub.InvokeAsync<FileOffer>(Protocol.ServerMethods.OfferFile, o),
            c => from.Hub.InvokeAsync(Protocol.ServerMethods.SendFileChunk, c),
            f => from.Hub.InvokeAsync(Protocol.ServerMethods.FinishFile, f),
            id => from.Hub.InvokeAsync(Protocol.ServerMethods.CancelFile, id),
            new MemoryStream(data),
            offer with { Size = data.Length });

    /// <summary>Rebuild what a receiver got, the way the agent does.</summary>
    private static async Task<byte[]?> Receive(Peer to, string transferId)
    {
        var offer = (FileOffer)(await to.WaitForAsync(Protocol.ClientMethods.FileOffered, a => ((FileOffer)a[0]!).TransferId == transferId))[0]!;
        var finish = (FileFinish)(await to.WaitForAsync(Protocol.ClientMethods.FileFinished, a => ((FileFinish)a[0]!).TransferId == transferId))[0]!;
        using var a = new FileAssembler(offer, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        foreach (var args in to.All(Protocol.ClientMethods.FileChunkReceived).Where(x => ((FileChunk)x[0]!).TransferId == transferId))
            if (!a.Append((FileChunk)args[0]!)) return null;
        using var data = a.Finish(finish.Sha256);
        if (data is null) return null;
        var copy = new MemoryStream();
        data.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task Teacher_sends_a_file_to_chosen_students_and_hears_back()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync("LAB-01");
        await using var s2 = await _factory.ConnectAsync();
        await s2.RegisterStudentAsync("LAB-02");
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        var data = RandomNumberGenerator.GetBytes(3 * FileLimits.ChunkSize + 7);
        var sent = await Send(teacher, data, new FileOffer { FileName = @"C:\x\worksheet.pdf", Targets = [s1.Id] });
        Assert.Equal([s1.Id], sent.Targets);
        Assert.Equal("worksheet.pdf", sent.FileName);

        Assert.Equal(data, await Receive(s1, sent.TransferId));
        await s2.AssertNoneAsync(Protocol.ClientMethods.FileOffered);

        await s1.Hub.InvokeAsync(Protocol.ServerMethods.ReportFileResult, new FileReceipt { TransferId = sent.TransferId, Ok = true });
        var receipt = (FileReceipt)(await teacher.WaitForAsync(Protocol.ClientMethods.FileResultReceived))[0]!;
        Assert.True(receipt.Ok);
        Assert.Equal(s1.Id, receipt.FromConnectionId);

        // Someone who wasn't a receiver can't report.
        await s2.Hub.InvokeAsync(Protocol.ServerMethods.ReportFileResult, new FileReceipt { TransferId = sent.TransferId, Ok = false });
        await Task.Delay(400);
        Assert.Single(teacher.All(Protocol.ClientMethods.FileResultReceived));
    }

    [Fact]
    public async Task Rules_executables_order_and_who_may_send()
    {
        // Strong controls reserved for code holders (not the default): open mode then sends no programs.
        await using var factory = new ServerFactory(("PROCTOR:AllowStrongControlsInOpenMode", "false"));
        await using var s1 = await factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var teacher = await factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();

        // No programs.
        await Assert.ThrowsAsync<HubException>(() => Send(teacher, [1, 2, 3], new FileOffer { FileName = "setup.exe" }));
        // Too big.
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync<FileOffer>(Protocol.ServerMethods.OfferFile,
            new FileOffer { FileName = "big.iso", Size = FileLimits.MaxSize + 1 }));
        // A student can't push files to anyone, nor hand in when nobody is collecting.
        await Assert.ThrowsAsync<HubException>(() => Send(s1, [1], new FileOffer { FileName = "a.txt" }));
        await Assert.ThrowsAsync<HubException>(() => Send(s1, [1], new FileOffer { FileName = "a.txt", HandIn = true }));

        // Chunks must come from the sender, in order, within the size.
        var offer = await teacher.Hub.InvokeAsync<FileOffer>(Protocol.ServerMethods.OfferFile, new FileOffer { FileName = "a.txt", Size = 4 });
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync(Protocol.ServerMethods.SendFileChunk,
            new FileChunk { TransferId = offer.TransferId, Index = 1, Data = [1] }));
        await Assert.ThrowsAsync<HubException>(() => s1.Hub.InvokeAsync(Protocol.ServerMethods.SendFileChunk,
            new FileChunk { TransferId = offer.TransferId, Index = 0, Data = [1] }));
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync(Protocol.ServerMethods.SendFileChunk,
            new FileChunk { TransferId = offer.TransferId, Index = 0, Data = [1, 2, 3, 4, 5] }));
        await Assert.ThrowsAsync<HubException>(() => teacher.Hub.InvokeAsync(Protocol.ServerMethods.FinishFile,
            new FileFinish { TransferId = offer.TransferId }));

        // The sender leaving mid-transfer cancels it for the receivers.
        await teacher.Hub.StopAsync();
        Assert.Equal(offer.TransferId, (string)(await s1.WaitForAsync(Protocol.ClientMethods.FileCancelled))[0]!);
    }

    [Fact]
    public async Task Students_hand_work_in_only_to_the_collecting_teacher()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync("LAB-05");
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        await using var other = await _factory.ConnectAsync();
        await other.RegisterTeacherAsync();

        Assert.Equal(1, await teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.RequestHandIn, new HandInRequest { Prompt = "Essay" }));
        var info = (HandInInfo)(await s1.WaitForAsync(Protocol.ClientMethods.HandInRequested))[0]!;
        Assert.True(info.Open);
        Assert.Equal("Essay", info.Prompt);

        await Assert.ThrowsAsync<HubException>(() => Send(s1, [1], new FileOffer { FileName = "virus.bat", HandIn = true }));
        var essay = "My essay"u8.ToArray();
        var sent = await Send(s1, essay, new FileOffer { FileName = "essay.txt", HandIn = true, Targets = [other.Id] });
        Assert.Equal([teacher.Id], sent.Targets);   // a student can't choose the receiver
        Assert.Equal(essay, await Receive(teacher, sent.TransferId));
        var offer = (FileOffer)teacher.All(Protocol.ClientMethods.FileOffered)[0][0]!;
        Assert.True(offer.HandIn);
        Assert.Equal("LAB-05", offer.FromName);
        await other.AssertNoneAsync(Protocol.ClientMethods.FileOffered);

        // Stop collecting: students are told, and further hand-ins are refused.
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.EndHandIn);
        await s1.WaitForAsync(Protocol.ClientMethods.HandInRequested, a => !((HandInInfo)a[0]!).Open);
        await Assert.ThrowsAsync<HubException>(() => Send(s1, [1], new FileOffer { FileName = "late.txt", HandIn = true }));
    }
}

public class FileSafetyTests : IAsyncLifetime
{
    private readonly ServerFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("macro.xlsm")]
    [InlineData("disk.iso")]
    [InlineData("disk.VHDX")]
    [InlineData("help.chm")]
    [InlineData("db.accde")]
    [InlineData("setup.appinstaller")]
    public void Risky_types_are_blocked(string name) => Assert.True(FileLimits.IsExecutable(name));

    [Fact]
    public void Saved_files_get_mark_of_the_web()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "x");
        try
        {
            FileLimits.MarkAsDownloaded(path);
            Assert.Contains("ZoneId=3", File.ReadAllText(path + ":Zone.Identifier"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task One_teacher_cannot_take_over_another_teachers_collection()
    {
        await using var s1 = await _factory.ConnectAsync();
        await s1.RegisterStudentAsync();
        await using var teacher = await _factory.ConnectAsync();
        await teacher.RegisterTeacherAsync();
        await using var rogue = await _factory.ConnectAsync();
        await rogue.RegisterTeacherAsync();

        Assert.Equal(1, await teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.RequestHandIn, new HandInRequest()));
        await Assert.ThrowsAsync<HubException>(() => rogue.Hub.InvokeAsync<int>(Protocol.ServerMethods.RequestHandIn, new HandInRequest()));

        // The original collector can ask again; once it stops, another teacher may collect.
        Assert.Equal(1, await teacher.Hub.InvokeAsync<int>(Protocol.ServerMethods.RequestHandIn, new HandInRequest()));
        await teacher.Hub.InvokeAsync(Protocol.ServerMethods.EndHandIn);
        Assert.Equal(1, await rogue.Hub.InvokeAsync<int>(Protocol.ServerMethods.RequestHandIn, new HandInRequest()));
    }
}
