using ClassInsight.Shared.Contracts;
using ClassInsight.Shared.Discovery;

namespace ClassInsight.Tests;

/// <summary>Discovery reply limits that a flood of fake replies can't defeat (NFR-SEC-06).</summary>
public class DiscoveryFloodTests
{
    private static DiscoveryResponse Reply(string host, int port = 5080, string? id = null, string? fingerprint = null) =>
        new() { Host = host, Port = port, ServerId = id ?? Guid.NewGuid().ToString("N"), Fingerprint = fingerprint };

    [Fact]
    public void One_host_cannot_flood_with_ports_or_server_ids()
    {
        var c = new DiscoveryReplyCollector();
        for (var i = 0; i < 500; i++) c.Offer(Reply("10.0.0.66", 5000 + i));
        Assert.True(c.Offer(Reply("10.0.0.5")));   // the real server still gets in
        Assert.Equal(LanDiscoveryClient.MaxRepliesPerHost, c.Results.Count(r => r.Host == "10.0.0.66"));
    }

    [Fact]
    public void A_reply_from_another_machine_cannot_shadow_a_server()
    {
        var c = new DiscoveryReplyCollector();
        Assert.True(c.Offer(Reply("10.0.0.66", id: "real-id")));   // a rogue claims the real server's id first
        Assert.True(c.Offer(Reply("10.0.0.5", id: "real-id")));    // the real server is still kept
    }

    [Fact]
    public void Preferred_address_survives_a_full_pool_but_claimed_fingerprints_do_not_jump_the_queue()
    {
        const string last = "10.0.0.5:5080";
        var pin = new string('a', 64);
        var c = new DiscoveryReplyCollector(r => $"{r.Host}:{r.Port}" == last);

        // Fill the general pool from many (spoofed) addresses, all advertising the pinned fingerprint.
        for (var i = 0; i < LanDiscoveryClient.MaxReplies + 50; i++)
            c.Offer(Reply($"10.1.{i / 250}.{i % 250}", fingerprint: pin));
        Assert.Equal(LanDiscoveryClient.MaxReplies, c.Results.Count);

        // The address that worked last time still gets in (its own slots)...
        Assert.True(c.Offer(Reply("10.0.0.5")));
        // ...but forged replies from that same address are capped per address too.
        Assert.True(c.Offer(Reply("10.0.0.5")));
        Assert.False(c.Offer(Reply("10.0.0.5")));
    }
}
