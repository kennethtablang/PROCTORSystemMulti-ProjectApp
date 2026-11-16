using ClassInsight.Shared.Contracts;

namespace ClassInsight.Shared.Discovery;

/// <summary>
/// Collects discovery replies with limits a flood can't defeat. Only a reply's source address is
/// real (<see cref="DiscoveryCodec"/>); port, room and server id are claims. Replies are keyed by
/// address + port + server id (one machine can't shadow another's server), at most
/// <see cref="LanDiscoveryClient.MaxRepliesPerHost"/> per address and
/// <see cref="LanDiscoveryClient.MaxReplies"/> in total. Replies the caller marks preferred — which
/// must be decided by the real source address, e.g. the address that worked last time — get their own
/// slots (<see cref="LanDiscoveryClient.MaxPreferred"/>, also per-address capped) and fall back to the
/// general pool when those are full.
/// </summary>
public sealed class DiscoveryReplyCollector(Func<DiscoveryResponse, bool>? preferred = null)
{
    private readonly Dictionary<string, DiscoveryResponse> _found = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _preferredPerHost = new(StringComparer.OrdinalIgnoreCase);
    private int _general;
    private int _favoured;

    /// <summary>Consider one reply; returns true if it was kept.</summary>
    public bool Offer(DiscoveryResponse r)
    {
        var key = $"{r.Host}|{r.Port}|{r.ServerId}";
        if (_found.ContainsKey(key)) return false;

        if (preferred?.Invoke(r) == true && _favoured < LanDiscoveryClient.MaxPreferred
            && _preferredPerHost.GetValueOrDefault(r.Host) < LanDiscoveryClient.MaxRepliesPerHost)
        {
            _preferredPerHost[r.Host] = _preferredPerHost.GetValueOrDefault(r.Host) + 1;
            _favoured++;
            _found[key] = r;
            return true;
        }

        if (_general >= LanDiscoveryClient.MaxReplies) return false;
        var n = _perHost.GetValueOrDefault(r.Host);
        if (n >= LanDiscoveryClient.MaxRepliesPerHost) return false;
        _perHost[r.Host] = n + 1;
        _general++;
        _found[key] = r;
        return true;
    }

    public IReadOnlyList<DiscoveryResponse> Results => _found.Values
        .OrderBy(r => r.RoomName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Host, StringComparer.Ordinal)
        .ToArray();
}
