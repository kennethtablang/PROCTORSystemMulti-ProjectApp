using System.Security.Cryptography;
using ClassInsight.Shared.Contracts;

namespace ClassInsight.Shared;

/// <summary>
/// Sends one file over the hub: offer → in-order chunks → finish with the SHA-256. Shared by the
/// teacher app (send files) and the agent (hand in work); the hub calls are passed in so this
/// library needs no SignalR reference.
/// </summary>
public static class FileSender
{
    public static async Task<FileOffer> SendAsync(
        Func<FileOffer, Task<FileOffer>> offer,
        Func<FileChunk, Task> chunk,
        Func<FileFinish, Task> finish,
        Func<string, Task> cancel,
        Stream source,
        FileOffer request,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        var accepted = await offer(request);
        var id = accepted.TransferId;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FileLimits.ChunkSize];
            long sent = 0;
            var index = 0;
            while (sent < request.Size)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, request.Size - sent);
                var read = await source.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false, ct);
                if (read == 0) throw new IOException("The file got shorter while it was being sent.");
                var data = buffer.AsSpan(0, read).ToArray();
                hash.AppendData(data);
                await chunk(new FileChunk { TransferId = id, Index = index++, Data = data });
                sent += read;
                progress?.Report(sent);
            }
            await finish(new FileFinish { TransferId = id, Sha256 = FileLimits.Hex(hash.GetHashAndReset()) });
            return accepted;
        }
        catch
        {
            try { await cancel(id); } catch { }
            throw;
        }
    }
}
