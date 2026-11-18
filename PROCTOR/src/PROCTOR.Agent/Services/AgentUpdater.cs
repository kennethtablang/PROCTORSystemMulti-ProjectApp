using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace PROCTOR.Agent.Services;

/// <summary>
/// Agent auto-update: student PCs follow the teacher's PC. After registering, the agent asks the
/// server (the teacher's PC) which agent version it carries; when that's newer, it downloads the
/// MSI over the TLS connection pinned to the server's key, checks the SHA-256 the hub reported,
/// and runs it silently. The MSI stops this service, upgrades it in place (keeping the lab
/// settings saved at the first install) and starts the new one. Students see nothing.
///
/// Only the installed service updates itself (never a console/test run), each PC waits a random
/// few minutes so 40+ PCs don't all download at once, and a failed version isn't retried for hours.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AgentUpdater
{
    private const long MaxSize = 400L * 1024 * 1024;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    private readonly ILogger _logger;
    private string? _attemptedVersion;
    private DateTimeOffset _attemptedAt;
    private int _running;

    public AgentUpdater(ILogger logger) => _logger = logger;

    public async Task CheckAsync(HubConnection hub, DiscoveryResponse server, string fingerprint, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            AgentUpdateInfo? info;
            try
            {
                info = await hub.InvokeAsync<AgentUpdateInfo?>(Protocol.ServerMethods.GetAgentUpdate, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "The server offers no agent updates (older server).");
                return;
            }

            var offered = ProductVersion.Parse(info?.Version);
            if (info is null || !ProductVersion.IsNewer(offered)) return;
            if (!WindowsServiceHelpers.IsWindowsService())
            {
                _logger.LogInformation("The teacher's PC offers agent {Version}; not updating a console run.", info.Version);
                return;
            }
            if (_attemptedVersion == info.Version && DateTimeOffset.UtcNow - _attemptedAt < RetryAfter) return;
            if (info.Size <= 0 || info.Size > MaxSize || info.Sha256.Length != 64) return;
            _attemptedVersion = info.Version;
            _attemptedAt = DateTimeOffset.UtcNow;

            // Spread the lab out: 40+ PCs fetching ~70 MB from one teacher PC at the same second
            // would saturate its link and slow its classroom view.
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 240)), ct);
            if (hub.State != HubConnectionState.Connected) return;

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PROCTOR", "updates");
            Directory.CreateDirectory(dir);   // inherits %ProgramData%\PROCTOR's protected ACL
            if (!ProtectedFiles.IsTrusted(new DirectoryInfo(dir)))
            {
                _logger.LogWarning("Not updating: {Dir} is writable by accounts other than SYSTEM and Administrators.", dir);
                return;
            }
            foreach (var old in Directory.EnumerateFiles(dir, "PROCTOR.Agent-*.msi"))
                try { File.Delete(old); } catch { }

            var path = Path.Combine(dir, $"PROCTOR.Agent-{info.Version}.msi");
            await DownloadAsync(server, fingerprint, info, path, ct);

            _logger.LogWarning("Updating the PROCTOR agent from {Old} to {New} (offered by the teacher's PC at {Host}).",
                ProductVersion.Text, info.Version, server.Host);
            var log = Path.Combine(dir, $"install-{info.Version}.log");
            using var msiexec = Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\" /qn /norestart /l*v \"{log}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            // The MSI now stops this service and starts the new version; nothing more to do here.
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent update failed; will retry later.");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static async Task DownloadAsync(DiscoveryResponse server, string fingerprint, AgentUpdateInfo info, string path, CancellationToken ct)
    {
        var url = new UriBuilder("https", server.Host, server.Port, Protocol.AgentUpdatePath).Uri;
        using var http = ServerTrust.PinnedHttpClient(fingerprint, TimeSpan.FromMinutes(15));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != info.Size)
            throw new InvalidDataException($"The server sent {length} bytes; it announced {info.Size}.");

        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > info.Size) throw new InvalidDataException("The download is larger than announced.");
                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total != info.Size) throw new InvalidDataException("The download is incomplete.");
            }
            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded agent package doesn't match its SHA-256.");
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }
}
