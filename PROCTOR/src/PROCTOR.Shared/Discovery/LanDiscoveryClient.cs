using System.Net;
using System.Net.Sockets;
using PROCTOR.Shared.Contracts;

namespace PROCTOR.Shared.Discovery;

/// <summary>
/// Zero-configuration LAN discovery used by both the student agent and the teacher app
/// to find classroom servers without any manual IP entry (AD-5, FR-DISC-03/04). It
/// broadcasts a <see cref="DiscoveryProbe"/> on UDP <see cref="Protocol.DiscoveryPort"/>
/// and listens for <see cref="DiscoveryResponse"/>s — direct replies to the probe as well
/// as the servers' periodic beacons.
///
/// Several rooms may share one network (FR-SRV-05): each is its own server, and
/// <see cref="FindServersAsync"/> returns every one heard within the window so a teacher can
/// choose (FR-DISC-04). The agent can be pinned to one room by name.
///
/// Every decoded response points at the datagram's real source address on the fixed hub
/// path (see <see cref="DiscoveryCodec.TryDecodeResponse"/>, NFR-SEC-05).
/// </summary>
public sealed class LanDiscoveryClient
{
    private readonly string _role;

    /// <param name="role">"student" or "teacher" — announced in the probe for logging/telemetry.</param>
    public LanDiscoveryClient(string role) => _role = role;

    /// <summary>
    /// Broadcast a probe and return the first server that answers within <paramref name="timeout"/>
    /// (optionally only one whose room is <paramref name="roomName"/>), or null.
    /// </summary>
    public async Task<DiscoveryResponse?> FindServerAsync(TimeSpan timeout, CancellationToken ct, string? roomName = null)
    {
        DiscoveryResponse? first = null;
        await ListenAsync(timeout, ct, r =>
        {
            if (!MatchesRoom(r, roomName)) return true;   // keep listening
            first = r;
            return false;                                  // done
        });
        return first;
    }

    /// <summary>
    /// Broadcast a probe and collect every distinct server heard within <paramref name="window"/>.
    /// Beacons repeat every <see cref="Protocol.BeaconIntervalMs"/>, so a window a little longer than
    /// that sees every room. Replies are keyed by their real source address, server id and port (a
    /// reply from one machine can't shadow a server first heard from another), with at most
    /// <see cref="MaxRepliesPerHost"/> per address and <see cref="MaxReplies"/> in total, so one noisy
    /// host can't flood the list. Replies the caller marks <paramref name="preferred"/> — use only
    /// something tied to the real source address, such as the address that worked last time, never a
    /// field the sender claims — have their own slots (<see cref="MaxPreferred"/>, at most
    /// <see cref="MaxRepliesPerHost"/> per address), so a flood that arrives first can't crowd them out.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveryResponse>> FindServersAsync(TimeSpan window, CancellationToken ct,
        Func<DiscoveryResponse, bool>? preferred = null)
    {
        var collector = new DiscoveryReplyCollector(preferred);
        await ListenAsync(window, ct, r =>
        {
            collector.Offer(r);
            return true;
        });
        return collector.Results;
    }

    /// <summary>Most servers one machine (source address) can contribute — two rooms on one PC is normal.</summary>
    public const int MaxRepliesPerHost = 2;

    /// <summary>Most ordinary replies kept from one discovery round (small records: memory only).</summary>
    public const int MaxReplies = 1024;

    /// <summary>Most replies kept that the caller marked as preferred (their own quota).</summary>
    public const int MaxPreferred = 64;

    /// <summary>How many broadcast addresses the last probe actually went out to (0 = no usable network).</summary>
    public int LastProbeTargets { get; private set; }

    public static bool MatchesRoom(DiscoveryResponse response, string? roomName) =>
        string.IsNullOrWhiteSpace(roomName)
        || string.Equals(response.RoomName, roomName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Probe, then feed each valid response to <paramref name="onResponse"/> until it returns false or time runs out.</summary>
    private async Task ListenAsync(TimeSpan window, CancellationToken ct, Func<DiscoveryResponse, bool> onResponse)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var probe = DiscoveryCodec.Encode(new DiscoveryProbe { Kind = "probe", Role = _role });
        LastProbeTargets = await SendProbeAsync(udp, probe);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(window);

        // Wi-Fi drops broadcasts far more often than wired links, and this socket hears only direct
        // replies to its own probe (beacons go to the discovery port), so one lost datagram would
        // waste the whole window. Re-probe a couple of times within it.
        var resend = ResendAsync(udp, probe, timeoutCts.Token);
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                var response = DiscoveryCodec.TryDecodeResponse(result.Buffer, result.RemoteEndPoint.Address);
                if (response is null) continue;
                if (!onResponse(response)) return;
            }
        }
        finally
        {
            // Stop re-probing before the socket is disposed.
            timeoutCts.Cancel();
            try { await resend; } catch { }
        }
    }

    /// <summary>Gap between probe repeats within one discovery window, and how many repeats.</summary>
    public static readonly TimeSpan ProbeRepeatInterval = TimeSpan.FromMilliseconds(700);
    public const int ProbeRepeats = 2;

    private async Task ResendAsync(UdpClient udp, byte[] probe, CancellationToken ct)
    {
        for (var i = 0; i < ProbeRepeats && !ct.IsCancellationRequested; i++)
        {
            try { await Task.Delay(ProbeRepeatInterval, ct); }
            catch (OperationCanceledException) { return; }
            await SendProbeAsync(udp, probe);
        }
    }

    /// <summary>
    /// Send the probe to the limited broadcast plus each subnet's directed broadcast: with a VPN or
    /// virtual adapter present, Windows sends 255.255.255.255 out of only one adapter, maybe not
    /// the lab's. Returns how many addresses it went out to.
    /// </summary>
    private static async Task<int> SendProbeAsync(UdpClient udp, byte[] probe)
    {
        var sent = 0;
        foreach (var target in LanAddresses.BroadcastAddresses())
        {
            try
            {
                await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, Protocol.DiscoveryPort));
                sent++;
            }
            catch (SocketException)
            {
                // Broadcast may be refused on some interfaces; the others still count.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
        return sent;
    }
}
