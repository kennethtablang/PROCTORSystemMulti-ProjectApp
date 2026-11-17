using System.Text.Json;
using Microsoft.Maui.Storage;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>A classroom server this teacher device has confirmed (NFR-SEC-06).</summary>
public sealed record TrustedServer
{
    public string Fingerprint { get; init; } = "";
    public string RoomName { get; init; } = "";
    public string Host { get; init; } = "";
    public DateTimeOffset TrustedAt { get; init; }
}

/// <summary>
/// Server keys this teacher has confirmed, kept on the device. A server is trusted by its TLS key
/// fingerprint (not its address or room name, which anyone could claim). An unknown key for a
/// room/address seen before means the server's identity changed — the teacher gets a warning.
/// </summary>
public static class TrustedServers
{
    private const string Key = "proctor.trusted-servers";
    private const int Max = 100;
    private static readonly object Gate = new();

    public static bool IsTrusted(string fingerprint)
    {
        lock (Gate) return Load().Any(t => t.Fingerprint == fingerprint);
    }

    /// <summary>A previously trusted key for this room name or address, if the presented one differs.</summary>
    public static TrustedServer? PreviousFor(string roomName, string host, string fingerprint)
    {
        lock (Gate)
            return Load().Where(t => t.Fingerprint != fingerprint)
                         .FirstOrDefault(t => string.Equals(t.RoomName, roomName, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Trust this key. A key replacing an older one for the same room/address drops the old one.</summary>
    public static void Trust(string fingerprint, string roomName, string host)
    {
        lock (Gate)
        {
            var list = Load().Where(t => t.Fingerprint != fingerprint
                                         && !string.Equals(t.RoomName, roomName, StringComparison.OrdinalIgnoreCase)
                                         && !string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, new TrustedServer { Fingerprint = fingerprint, RoomName = roomName, Host = host, TrustedAt = DateTimeOffset.UtcNow });
            try { Preferences.Default.Set(Key, JsonSerializer.Serialize(list.Take(Max).ToList())); } catch { }
        }
    }

    private static List<TrustedServer> Load()
    {
        try
        {
            var json = Preferences.Default.Get(Key, "");
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<TrustedServer>>(json) ?? [];
        }
        catch { return []; }
    }
}
