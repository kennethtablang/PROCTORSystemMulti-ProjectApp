using System.Globalization;

namespace PROCTOR.Shared.Contracts;

/// <summary>Teacher → server: wake these (switched-off) computers with a Wake-on-LAN magic packet.</summary>
public sealed record WakeRequest
{
    /// <summary>MAC addresses, e.g. <c>3C-52-82-1A-2B-3C</c> (any of <c>-</c>, <c>:</c> or no separator).</summary>
    public IReadOnlyList<string> MacAddresses { get; init; } = Array.Empty<string>();
}

public static class WakeOnLan
{
    /// <summary>Most MACs one request may wake (a large lab's worth).</summary>
    public const int MaxPerRequest = 128;

    /// <summary>Most MACs an agent may report.</summary>
    public const int MaxPerMachine = 4;

    /// <summary>Parse a unicast 48-bit MAC; false for anything else (broadcast, multicast, all-zero, junk).</summary>
    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().Replace("-", "").Replace(":", "").Replace(".", "");
        if (hex.Length != 12) return false;
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        if ((bytes[0] & 0x01) != 0 || bytes.All(b => b == 0)) return false;   // multicast/broadcast or empty
        mac = bytes;
        return true;
    }

    /// <summary>Canonical text form, e.g. <c>3C-52-82-1A-2B-3C</c>; null if not a valid unicast MAC.</summary>
    public static string? Normalize(string? text) =>
        TryParseMac(text, out var mac) ? string.Join("-", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : null;

    /// <summary>The standard magic packet: 6 × 0xFF followed by the MAC 16 times (102 bytes).</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("A MAC address is 6 bytes.", nameof(mac));
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var r = 0; r < 16; r++) Buffer.BlockCopy(mac, 0, packet, 6 + r * 6, 6);
        return packet;
    }
}
