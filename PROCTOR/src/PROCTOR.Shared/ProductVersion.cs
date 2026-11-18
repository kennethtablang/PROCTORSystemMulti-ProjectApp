using System.Reflection;

namespace PROCTOR.Shared;

/// <summary>
/// The PROCTOR system version (Directory.Build.props <c>ProctorVersion</c>), shared by every part, and
/// the parsing used to compare it with update offers ("v0.4.1" from a GitHub tag, "0.4.1" from the server).
/// </summary>
public static class ProductVersion
{
    public static readonly Version Current = Parse(
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? typeof(ProductVersion).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>"0.4.0" style, for display.</summary>
    public static string Text => Format(Current);

    public static string Format(Version v) => v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    /// <summary>Parse "v1.2.3", "1.2.3", "1.2.3+abc" or "1.2.3-beta" (pre-release suffix ignored); null if not a version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var cut = t.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return null;
        // Normalise to three parts so 0.4 == 0.4.0.
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static bool IsNewer(Version? candidate) => candidate is not null && candidate > Current;
}
