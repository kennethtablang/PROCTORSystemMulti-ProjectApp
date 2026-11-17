using System.Security.Cryptography;
using System.Text;
using Microsoft.Maui.Storage;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Optional PIN lock for the teacher app ("secure mode"): asked at launch and whenever the teacher
/// chooses "Lock this app now", so a student can't use an unattended teacher device. Only a salted
/// PBKDF2-SHA256 hash is stored (never the PIN), and checks are constant-time with a growing delay
/// after wrong attempts.
/// </summary>
public static class TeacherPin
{
    private const string HashKey = "proctor.pin.hash";
    private const string SaltKey = "proctor.pin.salt";
    private const int Iterations = 200_000;
    public const int MinLength = 4;
    public const int MaxLength = 12;

    private static int _failures;

    public static bool IsSet => !string.IsNullOrEmpty(Get(HashKey));

    public static bool IsValidFormat(string? pin) =>
        pin is { Length: >= MinLength and <= MaxLength } && pin.All(char.IsDigit);

    public static void Set(string pin)
    {
        if (!IsValidFormat(pin)) throw new ArgumentException($"A PIN is {MinLength}–{MaxLength} digits.");
        var salt = RandomNumberGenerator.GetBytes(16);
        Put(SaltKey, Convert.ToBase64String(salt));
        Put(HashKey, Convert.ToBase64String(Hash(pin, salt)));
        _failures = 0;
    }

    public static void Clear()
    {
        Put(HashKey, "");
        Put(SaltKey, "");
    }

    /// <summary>Check a PIN; after wrong attempts each further try waits longer (max 30 s).</summary>
    public static async Task<bool> VerifyAsync(string? pin)
    {
        if (!IsSet) return true;
        if (_failures > 2) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(_failures - 3, 4))));
        var salt = Convert.FromBase64String(Get(SaltKey));
        var expected = Convert.FromBase64String(Get(HashKey));
        var ok = CryptographicOperations.FixedTimeEquals(Hash(pin ?? "", salt), expected);
        _failures = ok ? 0 : _failures + 1;
        return ok;
    }

    private static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static string Get(string key)
    {
        try { return Preferences.Default.Get(key, ""); } catch { return ""; }
    }

    private static void Put(string key, string value)
    {
        try { Preferences.Default.Set(key, value); } catch { }
    }
}
