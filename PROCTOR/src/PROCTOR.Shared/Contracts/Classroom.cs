namespace PROCTOR.Shared.Contracts;

/// <summary>What a classroom-control command asks the student machine to do (P5).</summary>
public enum CommandKind
{
    /// <summary>Show a message from the teacher (FR-CTRL-03).</summary>
    Message = 0,
    /// <summary>Cover the screen and swallow keyboard input — "eyes on the teacher" (FR-CTRL-01).</summary>
    Lock = 1,
    /// <summary>Release a lock.</summary>
    Unlock = 2,
    /// <summary>Open a web page in the student's default browser (FR-CTRL-04, http/https only).</summary>
    OpenUrl = 3,
    /// <summary>Sign the student out of Windows.</summary>
    LogOff = 4,
    /// <summary>Restart the PC (with a short on-screen countdown so work can be saved).</summary>
    Restart = 5,
    /// <summary>Shut the PC down (with a short on-screen countdown).</summary>
    ShutDown = 6,
    /// <summary>Start a program for the student (Text = program name or full .exe path, no arguments).</summary>
    LaunchProgram = 7,
    /// <summary>Ask every open program in the student's session to close (they can still save).</summary>
    CloseAllPrograms = 8,
}

/// <summary>
/// A teacher's classroom-control command. The server validates and relays it; only registered
/// teachers can send one, and only to registered students.
/// </summary>
public sealed record ClassroomCommand
{
    public CommandKind Kind { get; init; }

    /// <summary>Student connection ids to target. Empty means every student in the room.</summary>
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();

    /// <summary>Message text (Message), or an optional caption shown on the lock screen (Lock).</summary>
    public string? Text { get; init; }

    /// <summary>Lock: the lock screen's heading (default "Eyes on your teacher").</summary>
    public string? Title { get; init; }

    /// <summary>Lock: the lock screen's background as "#RRGGBB" (default a dark purple).</summary>
    public string? Color { get; init; }

    /// <summary>Absolute http(s) URL (OpenUrl).</summary>
    public string? Url { get; init; }
}

/// <summary>How the room's web limits apply (FR-CTRL-06).</summary>
public enum WebLimitMode
{
    /// <summary>No web limits.</summary>
    Off = 0,
    /// <summary>The listed sites are blocked; everything else is allowed.</summary>
    BlockListed = 1,
    /// <summary>Only the listed sites are allowed.</summary>
    AllowOnlyListed = 2,
}

/// <summary>
/// Room-wide limits a teacher sets: web limits and blocked applications. The server pushes the
/// current policy to every student (including ones that join later). Website limits stay until a
/// teacher changes them (kept on disk across restarts, and on student PCs while the server is out
/// of reach); programs, internet and sound limits are cleared when the last teacher leaves, so a
/// crashed teacher app can't leave a lab without internet.
/// </summary>
public sealed record ClassPolicy
{
    public WebLimitMode WebMode { get; init; } = WebLimitMode.Off;

    /// <summary>Site hosts, e.g. <c>youtube.com</c> (also matches <c>www.youtube.com</c>, <c>m.youtube.com</c>).</summary>
    public IReadOnlyList<string> WebHosts { get; init; } = Array.Empty<string>();

    /// <summary>Program names to close, e.g. <c>steam</c> or <c>discord.exe</c>.</summary>
    public IReadOnlyList<string> BlockedApps { get; init; } = Array.Empty<string>();

    /// <summary>Block internet access (the lab LAN and the classroom server stay reachable).</summary>
    public bool BlockInternet { get; init; }

    /// <summary>Mute the student PCs' sound output.</summary>
    public bool MuteSound { get; init; }

    /// <summary>Block USB flash drives and memory cards: no new ones work, plugged-in ones are refused.</summary>
    public bool BlockUsbStorage { get; init; }

    /// <summary>An exam is running: students see an "Exam in progress" bar (with <see cref="ExamTitle"/>).</summary>
    public bool ExamMode { get; init; }

    /// <summary>The exam's name shown to students, e.g. "Quiz 3 — Networking".</summary>
    public string? ExamTitle { get; init; }

    /// <summary>True when the policy restricts nothing.</summary>
    public bool IsEmpty => !HasWebLimits && BlockedApps.Count == 0 && !BlockInternet && !MuteSound && !BlockUsbStorage && !ExamMode;

    /// <summary>The parts that end with the lesson (cleared when the last teacher leaves).</summary>
    public bool HasLessonLimits => BlockedApps.Count > 0 || BlockInternet || MuteSound || BlockUsbStorage || ExamMode;

    /// <summary>True when the website part restricts something.</summary>
    public bool HasWebLimits => !(WebMode == WebLimitMode.Off || (WebMode == WebLimitMode.BlockListed && WebHosts.Count == 0));

    /// <summary>The website part (mode + sites).</summary>
    public WebLimits Web => new() { WebMode = WebMode, WebHosts = WebHosts };

    /// <summary>This policy with its website part replaced (a student's own website limits).</summary>
    public ClassPolicy WithWeb(WebLimits? web) => web is null ? this : this with { WebMode = web.WebMode, WebHosts = web.WebHosts };
}

/// <summary>Website limits on their own: a whitelist ("allow only") or a blacklist ("block").</summary>
public sealed record WebLimits
{
    public WebLimitMode WebMode { get; init; } = WebLimitMode.Off;

    /// <summary>Site hosts, e.g. <c>youtube.com</c> (also matches its sub-domains).</summary>
    public IReadOnlyList<string> WebHosts { get; init; } = Array.Empty<string>();
}

/// <summary>Give selected students their own website limits; <see cref="Limits"/> null puts them back on the class's.</summary>
public sealed record StudentWebLimitsRequest
{
    /// <summary>Student connection ids (at least one).</summary>
    public IReadOnlyList<string> Students { get; init; } = Array.Empty<string>();

    public WebLimits? Limits { get; init; }
}

/// <summary>Limits and validation shared by the server (enforcement) and clients (early feedback).</summary>
public static class ClassroomLimits
{
    public const int MaxTextLength = 500;
    public const int MaxUrlLength = 2048;
    public const int MaxTargets = 500;
    public const int MaxPolicyHosts = 200;
    public const int MaxBlockedApps = 100;

    /// <summary>Seconds of warning students get before a remote restart / shut-down.</summary>
    public const int PowerCountdownSeconds = 30;

    /// <summary>A lock releases itself after this long even if no one unlocks it.</summary>
    public static readonly TimeSpan MaxLockDuration = TimeSpan.FromHours(2);

    /// <summary>
    /// Processes app-blocking will never close, whatever a policy says: Windows itself, the shell,
    /// and PROCTOR. Blocking any of these would break the session or the monitoring.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "lsaiso", "svchost", "dwm", "explorer", "sihost", "fontdrvhost", "ctfmon", "conhost",
        "runtimebroker", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "taskhostw", "dllhost", "audiodg", "spoolsv", "securityhealthsystray",
        "logonui", "lockapp", "userinit", "consent", "msmpeng",
        "proctor.agent", "proctor.server",
    };

    /// <summary>Printable text, trimmed and capped; null if nothing is left.</summary>
    public static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var chars = text.Where(c => c == (char)10 || !char.IsControl(c)).ToArray();
        var clean = new string(chars).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaxTextLength ? clean[..MaxTextLength] : clean;
    }

    public const int MaxLockTitleLength = 80;
    public const string DefaultLockTitle = "Eyes on your teacher";
    public const string DefaultLockMessage = "Your screen has been locked by your teacher.";
    public const string DefaultLockColor = "#14121E";

    /// <summary>A lock-screen heading: one line, at most <see cref="MaxLockTitleLength"/> characters, or null.</summary>
    public static string? CleanLockTitle(string? title)
    {
        var clean = CleanText(title?.Replace('\n', ' '));
        return clean is null ? null : clean.Length > MaxLockTitleLength ? clean[..MaxLockTitleLength] : clean;
    }

    /// <summary>A background colour as upper-case "#RRGGBB" ("#abc" and "RRGGBB" accepted), or null.</summary>
    public static string? CleanColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var hex = color.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return null;
        return "#" + hex.ToUpperInvariant();
    }

    /// <summary>Whether white text reads better than dark text on this "#RRGGBB" background.</summary>
    public static bool PrefersLightText(string color)
    {
        var hex = CleanColor(color) ?? DefaultLockColor;
        static double Channel(string h, int i)
        {
            var c = Convert.ToInt32(h.Substring(1 + i * 2, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(hex, 0) + 0.7152 * Channel(hex, 1) + 0.0722 * Channel(hex, 2);
        // Contrast against white vs against near-black (#111): pick the larger.
        return (1.05) / (luminance + 0.05) >= (luminance + 0.05) / (0.0056 + 0.05);
    }

    /// <summary>
    /// Accept only absolute http/https URLs of sane length with a host — never file:, javascript:,
    /// custom protocol handlers or anything that could launch a local program.
    /// </summary>
    public static bool TryCleanUrl(string? url, out string clean)
    {
        clean = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var text = url.Trim();
        if (text.Length > MaxUrlLength) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        if (uri.AbsoluteUri.Any(c => c is '"' or ' ' || char.IsControl(c))) return false;
        clean = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Normalise a site entry to a bare lower-case host: accepts "YouTube.com", "https://www.x.org/a",
    /// "*.example.com". Returns null for anything that isn't a plausible host name.
    /// </summary>
    public static string? CleanHost(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var text = entry.Trim().ToLowerInvariant();
        if (text.StartsWith("*.", StringComparison.Ordinal)) text = text[2..];
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        var host = uri.Host.TrimEnd('.');
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host.Length is > 0 and <= 253 && (host.Contains('.') || host == "localhost") ? host : null;
    }

    /// <summary>
    /// True when <paramref name="host"/> is one of <paramref name="listed"/> or a subdomain of one
    /// ("m.youtube.com" matches "youtube.com"; "notyoutube.com" does not).
    /// </summary>
    public static bool HostMatches(string host, IEnumerable<string> listed)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.ToLowerInvariant().TrimEnd('.');
        foreach (var entry in listed)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            if (host == entry || host.EndsWith("." + entry, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a site is allowed under <paramref name="policy"/>. Browser start pages / new tabs
    /// have no host and are always allowed, as is a PROCTOR-opened page's own host.
    /// </summary>
    public static bool IsSiteAllowed(string? host, ClassPolicy policy)
    {
        if (string.IsNullOrEmpty(host) || policy.WebMode == WebLimitMode.Off) return true;
        var listed = HostMatches(host, policy.WebHosts);
        return policy.WebMode == WebLimitMode.BlockListed ? !listed : listed;
    }

    /// <summary>Normalise a program name ("Steam.exe" → "steam"); null if empty or protected.</summary>
    public static string? CleanAppName(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var name = Path.GetFileName(entry.Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        name = name.Trim().ToLowerInvariant();
        if (name.Length is 0 or > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return ProtectedApps.Contains(name) ? null : name;
    }

    /// <summary>Validate and normalise website limits (unknown mode → off, dedupe and cap the sites).</summary>
    public static WebLimits CleanWeb(WebLimits? web)
    {
        if (web is null) return new WebLimits();
        return new WebLimits
        {
            WebMode = Enum.IsDefined(web.WebMode) ? web.WebMode : WebLimitMode.Off,
            WebHosts = web.WebHosts.Select(CleanHost).OfType<string>().Distinct().Take(MaxPolicyHosts).ToArray(),
        };
    }

    /// <summary>Validate and normalise a whole policy (dedupe, cap sizes, drop protected apps).</summary>
    public static ClassPolicy Clean(ClassPolicy? policy)
    {
        if (policy is null) return new ClassPolicy();
        var hosts = policy.WebHosts.Select(CleanHost).OfType<string>().Distinct().Take(MaxPolicyHosts).ToArray();
        var apps = policy.BlockedApps.Select(CleanAppName).OfType<string>().Distinct().Take(MaxBlockedApps).ToArray();
        var mode = Enum.IsDefined(policy.WebMode) ? policy.WebMode : WebLimitMode.Off;
        return new ClassPolicy
        {
            WebMode = mode,
            WebHosts = hosts,
            BlockedApps = apps,
            BlockInternet = policy.BlockInternet,
            MuteSound = policy.MuteSound,
            BlockUsbStorage = policy.BlockUsbStorage,
            ExamMode = policy.ExamMode,
            ExamTitle = policy.ExamMode ? CleanLockTitle(policy.ExamTitle) : null,
        };
    }

    // ---- programs (LaunchProgram) -----------------------------------------------------------

    public const int MaxProgramLength = 260;

    /// <summary>Friendly names a teacher can type, mapped to the program Windows starts.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProgramAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["word"] = "winword.exe",
        ["excel"] = "excel.exe",
        ["powerpoint"] = "powerpnt.exe",
        ["access"] = "msaccess.exe",
        ["publisher"] = "mspub.exe",
        ["outlook"] = "outlook.exe",
        ["edge"] = "msedge.exe",
        ["chrome"] = "chrome.exe",
        ["firefox"] = "firefox.exe",
        ["notepad"] = "notepad.exe",
        ["paint"] = "mspaint.exe",
        ["calculator"] = "calc.exe",
        ["file explorer"] = "explorer.exe",
        ["explorer"] = "explorer.exe",
        ["command prompt"] = "cmd.exe",
        ["vs code"] = "code",
        ["visual studio code"] = "code",
    };

    /// <summary>
    /// A program to start: a friendly name ("Word"), a program name ("winword.exe", "code") or a
    /// full path to an .exe — never arguments or shell syntax, so nothing else can be smuggled in.
    /// Returns what the agent passes to Windows' "start", or null.
    /// </summary>
    public static string? CleanProgram(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        var p = program.Trim().Trim('"').Trim();
        if (ProgramAliases.TryGetValue(p, out var alias)) return alias;
        if (p.Length is 0 or > MaxProgramLength) return null;
        // Nothing a shell would interpret, and no arguments.
        if (p.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!', ';', '`', '\n', '\r', '\t', '*', '?']) >= 0) return null;
        if (p.Contains(@"\\") || p.StartsWith('-') || p.StartsWith('/')) return null;   // no network shares or switches

        var isPath = p.Contains('\\') || p.Contains(':');
        if (isPath)
        {
            // C:\...\app.exe only: a rooted local path to an executable.
            if (p.Length < 7 || !char.IsLetter(p[0]) || p[1] != ':' || p[2] != '\\') return null;
            if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (p.Contains("..")) return null;
            return p;
        }

        // A bare program name: letters, digits, space, dot, dash, underscore, plus.
        if (!p.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '+')) return null;
        if (p.Contains(' ') && !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;   // "foo bar" would be "foo" + an argument
        return p;
    }
}
