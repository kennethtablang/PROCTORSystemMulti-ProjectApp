using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// The teacher side of WebRTC screen viewing for one student. Created and driven by
/// <see cref="StreamManager"/>, which routes the student's relayed offer/ICE here. It asks the
/// server for a stream at a given quality/display, answers the student's SDP offer, and decodes
/// each incoming VP8 frame into the student's <see cref="FrameBuffer"/>.
///
/// A quality or display switch is a re-request: the agent replaces its sender and offers again,
/// and this receiver swaps in a fresh peer connection (and decoder) for the new offer, so the
/// tile keeps showing the last frame until the new stream's first keyframe lands.
///
/// Mirrors <c>ScreenStreamSender</c> on the agent (student = offerer, teacher = answerer). The
/// server relays SDP/ICE only between this authorised teacher↔student pair (FR-SEC-04); media is
/// DTLS-SRTP encrypted end-to-end (NFR-SEC-03). The VP8 codec is pure managed, so decoding is
/// identical on every teacher platform (NFR-PORT-01).
/// </summary>
public sealed class ScreenStreamReceiver : IAsyncDisposable
{
    private readonly TeacherClient _client;
    private readonly StudentTile _tile;
    private readonly Action<string> _status;
    private readonly object _gate = new();

    // Per-quality frame caps (see OnVideoFrame). Worst case for 40 thumbnails ≈ 40 × 640×640×4 ≈ 65 MB.
    private const int MaxThumbW = 640, MaxThumbH = 640;
    private const int MaxFocusW = 1920, MaxFocusH = 1920;

    private RTCPeerConnection? _pc;
    private VP8Codec? _decoder;
    private volatile bool _disposed;

    public ScreenStreamReceiver(TeacherClient client, StudentTile tile, Action<string> status)
    {
        _client = client;
        _tile = tile;
        _status = status;
    }

    public string StudentConnectionId => _tile.ConnectionId;
    public StreamQuality Quality { get; private set; }
    public int DisplayIndex { get; private set; }

    /// <summary>When the current request was sent; used to retry a request that never got an offer.</summary>
    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>True once the peer connection is up.</summary>
    public bool IsConnected => _pc?.connectionState == RTCPeerConnectionState.connected;

    /// <summary>True when the stream died on its own (not torn down by us) and should be retried.</summary>
    public bool Failed { get; private set; }

    /// <summary>True while waiting for the agent's offer to the current request.</summary>
    public bool AwaitingOffer { get; private set; }

    /// <summary>Ask the server for a stream at this quality/display (also used to switch).</summary>
    public async Task RequestAsync(StreamQuality quality, int displayIndex)
    {
        Quality = quality;
        DisplayIndex = displayIndex;
        RequestedAt = DateTimeOffset.UtcNow;
        Failed = false;
        AwaitingOffer = true;
        _status(IsConnected ? "Switching…" : "Requesting stream…");

        await _client.SendAsync(Protocol.ServerMethods.RequestStream, new StreamRequest
        {
            StudentConnectionId = StudentConnectionId,
            Quality = quality,
            DisplayIndex = displayIndex,
        });
    }

    /// <summary>The student's SDP offer (initial, or a renegotiation after a switch).</summary>
    public async Task HandleOfferAsync(SdpMessage offer)
    {
        if (_disposed) return;

        RTCPeerConnection pc;
        RTCSessionDescriptionInit answer;
        lock (_gate)
        {
            // A new offer always supersedes the previous stream.
            ClosePeerLocked();

            var decoder = new VP8Codec();
            pc = new RTCPeerConnection(BuildConfig(_client.IceServers));

            // Receive-only VP8 video track that matches what the agent offers.
            pc.addTrack(new MediaStreamTrack(decoder.SupportedFormats, MediaStreamStatusEnum.RecvOnly));

            // Reassembled VP8 focus frames can be large; give the depacketiser headroom.
            // MUST come after addTrack — the setter dereferences the video stream, which
            // doesn't exist until a track is added (verified: NRE otherwise).
            pc.MaxReconstructedVideoFrameSize = 2_000_000;

            pc.onicecandidate += candidate =>
            {
                if (candidate is null) return;
                _ = _client.SendAsync(Protocol.ServerMethods.SendIceCandidate, new IceCandidateMessage
                {
                    TargetConnectionId = StudentConnectionId,
                    Candidate = candidate.ToString(),
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                });
            };

            pc.OnVideoFrameReceived += (_, _, frame, _) => OnVideoFrame(pc, decoder, frame);

            pc.onconnectionstatechange += state =>
            {
                if (!ReferenceEquals(pc, _pc)) return;   // a superseded connection closing
                switch (state)
                {
                    case RTCPeerConnectionState.connected:
                        _status("Live");
                        break;
                    case RTCPeerConnectionState.connecting:
                        _status("Connecting…");
                        break;
                    case RTCPeerConnectionState.failed:
                    case RTCPeerConnectionState.disconnected:
                    case RTCPeerConnectionState.closed:
                        // During a quality/display switch the agent closes the old peer before the
                        // new offer lands; that's expected, not a failure to retry.
                        if (!_disposed && !AwaitingOffer)
                        {
                            Failed = true;
                            _status(state == RTCPeerConnectionState.failed ? "Connection failed." : "Stream ended.");
                        }
                        break;
                }
            };

            var set = pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offer.Sdp,
            });
            if (set != SetDescriptionResultEnum.OK)
            {
                _status($"Could not accept offer: {set}");
                pc.close();
                decoder.Dispose();
                Failed = true;
                return;
            }

            answer = pc.createAnswer();
            _pc = pc;
            _decoder = decoder;
            AwaitingOffer = false;
        }

        await pc.setLocalDescription(answer);
        await _client.SendAsync(Protocol.ServerMethods.SendAnswer, new SdpMessage
        {
            TargetConnectionId = StudentConnectionId,
            Type = "answer",
            Sdp = answer.sdp,
        });
        _status("Negotiating…");
    }

    public void HandleIceCandidate(IceCandidateMessage message)
    {
        RTCPeerConnection? pc;
        lock (_gate) pc = _pc;
        pc?.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = (ushort)(message.SdpMLineIndex ?? 0),
        });
    }

    private void OnVideoFrame(RTCPeerConnection source, VP8Codec decoder, byte[] frame)
    {
        if (_disposed || !ReferenceEquals(source, _pc)) return;

        // Reject an oversized stream *before* the decoder allocates for it: a VP8 keyframe
        // declares its size in its header, and inter frames can't change it.
        if (!KeyframeWithinCap(frame)) return;

        try
        {
            // Decode the VP8 frame to BGR. DecodeVideo yields nothing until the first keyframe
            // arrives, which is expected.
            foreach (var sample in decoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8))
            {
                var w = (int)sample.Width;
                var h = (int)sample.Height;
                // Clamp to what we asked for before any sizing, so a hostile/buggy sender can't
                // overflow the size math or make the teacher allocate huge buffers — 40 thumbnail
                // tiles each forced to 8K would be gigabytes. The agent sends 480 px thumbnails
                // and 1280 px focus frames; the caps leave headroom for other agent builds.
                var (maxW, maxH) = Quality == StreamQuality.Focus ? (MaxFocusW, MaxFocusH) : (MaxThumbW, MaxThumbH);
                if (w <= 0 || w > maxW || h <= 0 || h > maxH || sample.Sample is null) continue;
                if (sample.Sample.Length < (long)w * h * 3) continue;

                _tile.Frame.WriteBgr(w, h, sample.Sample);
            }
        }
        catch
        {
            // A corrupt frame shouldn't kill the stream; the next keyframe recovers it.
        }
    }

    /// <summary>
    /// VP8 keyframe header (RFC 6386 §9.1): 3-byte frame tag (bit 0 of byte 0 is 0 for a keyframe),
    /// start code 9D 01 2A, then 14-bit width and height. Non-keyframes pass (they inherit the
    /// already-checked keyframe size); a keyframe larger than the cap for the requested quality is
    /// dropped, so the decoder never sizes buffers for it.
    /// </summary>
    private bool KeyframeWithinCap(byte[] frame)
    {
        if (frame.Length < 10 || (frame[0] & 0x01) != 0) return true;
        if (frame[3] != 0x9D || frame[4] != 0x01 || frame[5] != 0x2A) return false;   // malformed keyframe
        var w = (frame[6] | (frame[7] << 8)) & 0x3FFF;
        var h = (frame[8] | (frame[9] << 8)) & 0x3FFF;
        var (maxW, maxH) = Quality == StreamQuality.Focus ? (MaxFocusW, MaxFocusH) : (MaxThumbW, MaxThumbH);
        return w > 0 && h > 0 && w <= maxW && h <= maxH;
    }

    internal static RTCConfiguration? BuildConfig(IReadOnlyList<IceServerConfig> servers)
    {
        if (servers.Count == 0) return null;   // flat LAN: host candidates are enough
        return new RTCConfiguration
        {
            iceServers = servers.Select(s => new RTCIceServer
            {
                urls = s.Urls,
                username = s.Username,
                credential = s.Credential,
            }).ToList(),
        };
    }

    private void ClosePeerLocked()
    {
        var pc = _pc;
        var decoder = _decoder;
        _pc = null;
        _decoder = null;
        try { pc?.close(); } catch { }
        // Let any in-flight decode finish before freeing the codec.
        if (decoder is not null) _ = Task.Delay(500).ContinueWith(_ => decoder.Dispose());
    }

    /// <summary>Stop viewing: tell the server (so the agent stops encoding, FR-STREAM-06) and close the peer.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _client.SendAsync(Protocol.ServerMethods.StopStream, StudentConnectionId);

        lock (_gate) ClosePeerLocked();
    }
}
