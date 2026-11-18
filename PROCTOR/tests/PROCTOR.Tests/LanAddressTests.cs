using System.Net;
using PROCTOR.Shared;
using PROCTOR.Shared.Discovery;

namespace PROCTOR.Tests;

/// <summary>The address shown to teachers / advertised by the server, and where discovery broadcasts go.</summary>
public class LanAddressTests
{
    [Fact]
    public void Broadcast_targets_start_with_the_limited_broadcast_and_have_no_duplicates()
    {
        var targets = LanAddresses.BroadcastAddresses();
        Assert.Equal(IPAddress.Broadcast, targets[0]);
        Assert.Equal(targets.Count, targets.Distinct().Count());
    }

    [Fact]
    public void Broadcast_targets_are_subnet_broadcasts_of_this_pcs_addresses()
    {
        // Every directed broadcast must end in all-ones host bits for one of this PC's subnets,
        // never a unicast address (which would send discovery to one random machine).
        foreach (var target in LanAddresses.BroadcastAddresses().Skip(1))
        {
            var bytes = target.GetAddressBytes();
            Assert.NotEqual(0, bytes[3] & 1);   // a /30 or wider broadcast always ends in a set bit
        }
    }

    [Fact]
    public void Best_address_is_never_loopback_and_prefers_an_adapter_with_a_gateway()
    {
        var all = LanAddresses.Find();
        var best = LanAddresses.Best();
        if (best is null) { Assert.Empty(all); return; }   // PC without any IPv4 network

        Assert.False(IPAddress.IsLoopback(best.Address));
        if (all.Any(c => c.HasGateway)) Assert.True(best.HasGateway);
    }

    [Fact]
    public void Each_discovery_window_repeats_the_probe()
    {
        // One lost Wi-Fi datagram must not waste the whole window: the repeats fit inside the
        // shortest window clients use (agent 3 s; teacher beacon interval + 600 ms).
        var lastRepeat = LanDiscoveryClient.ProbeRepeatInterval * LanDiscoveryClient.ProbeRepeats;
        Assert.True(LanDiscoveryClient.ProbeRepeats >= 2);
        Assert.True(lastRepeat < TimeSpan.FromMilliseconds(Protocol.BeaconIntervalMs + 600) - TimeSpan.FromMilliseconds(500));
    }
}
