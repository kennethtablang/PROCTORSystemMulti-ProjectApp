using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using PROCTOR.Shared;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>A PROCTOR release published on GitHub.</summary>
public sealed record AppRelease(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    string? SetupUrl,
    long SetupSize,
    string? SetupSha256Url)
{
    public string VersionText => ProductVersion.Format(Version);
    public bool IsNewer => ProductVersion.IsNewer(Version);
}

/// <summary>
/// Auto-update for the teacher's PC (and, through it, the lab). Checks the project's latest
/// GitHub release; when it is newer than this app, the update pop-up offers it. "Update now"
/// downloads <see cref="SetupAssetName"/> from the release, verifies it, closes the app, runs the
/// setup (which also updates the classroom server and the agent package it hands to student
/// PCs) and reopens the app.
///
/// The repository is public, so no credentials are involved. One check per teacher PC every few
/// hours is far below GitHub's unauthenticated limit; student PCs never call GitHub.
/// </summary>
public static class AppUpdater
{
    public const string Repository = "kennethtablang/PROCTORSystemMulti-ProjectApp";
    public const string ProjectUrl = "https://github.com/" + Repository;
    public const string ReleasesUrl = ProjectUrl + "/releases";
    public const string IssuesUrl = ProjectUrl + "/issues";
    public const string SetupAssetName = "PROCTOR-Teacher-Setup.exe";

    private const long MaxSetupSize = 2L * 1024 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API requires a User-Agent; a generic product name, nothing about this school or PC.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PROCTOR-Teacher", ProductVersion.Text));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>Installing an update in place is possible only for the Windows teacher app.</summary>
    public static bool CanInstall => OperatingSystem.IsWindows();

    /// <summary>The latest published release, or null when it can't be read (offline, no releases).</summary>
    public static async Task<AppRelease?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (ProductVersion.Parse(tag) is not { } version) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            string? setupUrl = null, shaUrl = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (string.Equals(name, SetupAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        setupUrl = url;
                        size = a.GetProperty("size").GetInt64();
                    }
                    else if (string.Equals(name, SetupAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                    {
                        shaUrl = url;
                    }
                }
            }

            return new AppRelease(
                version,
                tag,
                root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title ? title : $"PROCTOR {ProductVersion.Format(version)}",
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var h) && h.GetString() is { } page ? page : ReleasesUrl,
                IsGitHubDownload(setupUrl) ? setupUrl : null,
                size,
                IsGitHubDownload(shaUrl) ? shaUrl : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Only ever download from this project's own release assets, over HTTPS.</summary>
    private static bool IsGitHubDownload(string? url) =>
        url is not null
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Download the release's setup to a fresh folder in %TEMP% and verify it: the size GitHub
    /// reports, and the SHA-256 published with it (<c>.sha256</c> asset) when there is one.
    /// </summary>
    public static async Task<string> DownloadSetupAsync(AppRelease release, IProgress<double> progress, CancellationToken ct)
    {
        if (release.SetupUrl is null) throw new InvalidOperationException("This release has no teacher setup to download.");
        if (release.SetupSize <= 0 || release.SetupSize > MaxSetupSize) throw new InvalidOperationException("The release's setup has an unexpected size.");

        string? expectedHash = null;
        if (release.SetupSha256Url is not null)
        {
            var text = await Http.GetStringAsync(release.SetupSha256Url, ct);
            expectedHash = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
            if (expectedHash is not { Length: 64 }) throw new InvalidOperationException("The release's checksum file is malformed.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "PROCTOR-Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SetupAssetName);

        using var download = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        download.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PROCTOR-Teacher", ProductVersion.Text));
        using var response = await download.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The download was redirected away from HTTPS.");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > release.SetupSize) throw new InvalidOperationException("The download is larger than the release says.");
                sha.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                progress.Report((double)total / release.SetupSize);
            }
            if (total != release.SetupSize) throw new InvalidOperationException("The download is incomplete.");
        }

        var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (expectedHash is not null && hash != expectedHash)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException("The downloaded setup doesn't match the release's checksum.");
        }
        return path;
    }

    /// <summary>
    /// Run the downloaded setup and close this app. A small hidden helper waits for the app to exit,
    /// runs the setup with its progress window (Windows asks for administrator approval once), then
    /// reopens the app, not elevated.
    /// </summary>
    public static void InstallAndExit(string setupPath)
    {
        if (!CanInstall) throw new PlatformNotSupportedException();
        var app = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where this app is installed.");
        // ping = a wait that works without a console (timeout.exe needs one).
        var script = $"ping -n 4 127.0.0.1 >nul & start \"\" /wait \"{setupPath}\" /passive /norestart & start \"\" \"{app}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Application.Current?.Quit();
    }

    // ---- "Later" -------------------------------------------------------------------------------

    private const string SnoozeKey = "proctor.update.snooze";

    /// <summary>"Later": don't offer this version again for a day (a newer one still shows).</summary>
    public static void Snooze(AppRelease release)
    {
        try { Preferences.Default.Set(SnoozeKey, $"{release.VersionText}|{DateTimeOffset.UtcNow.AddDays(1):O}"); } catch { }
    }

    public static bool IsSnoozed(AppRelease release)
    {
        try
        {
            var parts = Preferences.Default.Get(SnoozeKey, "").Split('|');
            return parts.Length == 2 && parts[0] == release.VersionText
                && DateTimeOffset.TryParse(parts[1], out var until) && DateTimeOffset.UtcNow < until;
        }
        catch { return false; }
    }
}
