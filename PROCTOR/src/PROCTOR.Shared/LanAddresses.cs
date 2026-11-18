using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PROCTOR.Shared;

/// <summary>
/// This PC's IPv4 addresses that other PCs on the LAN can dial, best first. The adapter carrying
/// the default gateway (the network actually in use) beats gateway-less ones such as VPN taps,
/// Hyper-V / VirtualBox / VMware switches and Bluetooth; real adapters beat virtual ones; wired
/// beats Wi-Fi; link-local 169.254.x.x comes last. Shared by the server (the address it
/// advertises) and the teacher app (the address it tells the teacher to give student PCs), so the
/// two always agree.
/// </summary>
public static class LanAddresses
{
    public sealed record Candidate(IPAddress Address, string AdapterName, NetworkInterfaceType Type, bool HasGateway)
    {
        public bool IsWireless => Type == NetworkInterfaceType.Wireless80211;
    }

    private static readonly string[] VirtualHints =
        ["virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "tap-", "wintun", "wireguard", "vpn",
         "outline", "tailscale", "zerotier", "bluetooth", "pseudo", "docker", "wsl"];

    public static IReadOnlyList<Candidate> Find()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception) { return []; }

        var found = new List<(Candidate C, int Score)>();
        foreach (var nic in nics)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                var hasGateway = HasIPv4Gateway(props);
                var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                var isVirtual = VirtualHints.Any(text.Contains);

                foreach (var ua in props.UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                    var b = a.GetAddressBytes();
                    var linkLocal = b[0] == 169 && b[1] == 254;

                    var score = (linkLocal ? 0 : 1000)
                              + (hasGateway ? 100 : 0)
                              + (isVirtual ? 0 : 10)
                              + nic.NetworkInterfaceType switch
                              {
                                  NetworkInterfaceType.Ethernet => 3,
                                  NetworkInterfaceType.Wireless80211 => 2,
                                  _ => 1,
                              };
                    found.Add((new Candidate(a, nic.Name, nic.NetworkInterfaceType, hasGateway), score));
                }
            }
            catch (Exception)
            {
                // Some platforms don't expose every property (e.g. gateways on iOS); skip that adapter.
            }
        }
        return found.OrderByDescending(x => x.Score).Select(x => x.C).ToList();
    }

    /// <summary>The address to advertise / show, or null when the PC has no IPv4 network at all.</summary>
    public static Candidate? Best() => Find().FirstOrDefault();

    /// <summary>
    /// Where to send a LAN broadcast: the limited broadcast 255.255.255.255 <b>and</b> every up IPv4
    /// subnet's directed broadcast (e.g. 192.168.2.255). On a PC with several adapters — a VPN, a
    /// Hyper-V / VirtualBox switch, Wi-Fi plus Ethernet — Windows sends 255.255.255.255 out of just
    /// one of them, which may not be the lab network; a directed broadcast always leaves through
    /// the adapter that owns that subnet.
    /// </summary>
    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception) { return list; }

        foreach (var nic in nics)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    var mask = ua.IPv4Mask?.GetAddressBytes();
                    // A /32 (point-to-point VPN) or /31 has no broadcast address; an all-zero mask is unknown.
                    if (mask is null || mask.All(b => b == 0) || mask.All(b => b == 255) || ua.PrefixLength >= 31) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    var address = new IPAddress(bc);
                    if (!list.Contains(address)) list.Add(address);
                }
            }
            catch (Exception)
            {
                // Mask / prefix not exposed on this platform or adapter: the limited broadcast still goes out.
            }
        }
        return list;
    }

    private static bool HasIPv4Gateway(IPInterfaceProperties props)
    {
        try
        {
            return props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
