using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Server.Services;

/// <summary>
/// Sends Wake-on-LAN magic packets from the server, which sits on the lab network (a teacher's
/// tablet may not). Each packet goes to the limited broadcast address and to every local IPv4
/// subnet's directed broadcast, on UDP ports 9 and 7. Rate-limited so a teacher connection can't
/// turn the server into a packet hose.
/// </summary>
public sealed class WakeSender
{
    private static readonly int[] Ports = [9, 7];
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;

    /// <summary>Minimum gap between wake requests (server-wide).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    public WakeSender() : this(TimeProvider.System) { }
    public WakeSender(TimeProvider clock) => _clock = clock;

    /// <summary>Reserve a send slot; false while the previous request is too recent.</summary>
    public bool TryReserve()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now - _lastSend < MinInterval) return false;
            _lastSend = now;
            return true;
        }
    }

    /// <summary>Send a magic packet for each MAC (already validated). Returns how many were sent.</summary>
    public async Task<int> SendAsync(IReadOnlyList<byte[]> macs, ILogger logger)
    {
        var targets = BroadcastAddresses();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var sent = 0;
        foreach (var mac in macs)
        {
            var packet = WakeOnLan.MagicPacket(mac);
            var ok = false;
            foreach (var address in targets)
                foreach (var port in Ports)
                {
                    try { await udp.SendAsync(packet, new IPEndPoint(address, port)); ok = true; }
                    catch (SocketException ex) { logger.LogDebug(ex, "Wake packet to {Address}:{Port} failed.", address, port); }
                }
            if (ok) sent++;
        }
        return sent;
    }

    private static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    var address = new IPAddress(bc);
                    if (!list.Contains(address)) list.Add(address);
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }
}
