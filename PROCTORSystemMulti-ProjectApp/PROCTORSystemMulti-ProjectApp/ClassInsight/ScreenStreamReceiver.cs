using ClassInsight.Shared;
using ClassInsight.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// The teacher side of WebRTC screen viewing for a single student (the focus view).
/// Asks the server to start the stream, answers the student's SDP offer, exchanges ICE
/// over the same SignalR hub, and decodes each incoming VP8 frame to raw BGRA pixels
/// that the UI blits to a canvas.
///
/// Mirrors <c>ScreenStreamSender</c> on the agent: there the student is the media
/// sender/offerer; here the teacher is the receiver/answerer. The server relays SDP/ICE
/// only between this authorised teacher↔student pair (FR-SEC-04).
///
/// The VP8 codec is pure managed (<see cref="VP8Codec"/>), so decoding works the same on
/// every teacher platform with no native dependency (NFR-PORT-01).
/// </summary>
public sealed class ScreenStreamReceiver : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly string _studentConnectionId;
    private readonly StreamQuality _quality;

    private readonly VP8Codec _decoder = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RTCPeerConnection? _pc;
    private byte[]? _bgra;          // reusable decode target, grown as needed
    private volatile bool _disposed;

    /// <summary>Raised with a freshly decoded frame as 32-bpp BGRA, top-down, tightly packed.</summary>
    public event Action<DecodedFrame>? FrameDecoded;

    /// <summary>Human-readable status for the focus view (connecting, streaming, failed…).</summary>
    public event Action<string>? Status;

    public ScreenStreamReceiver(HubConnection hub, string studentConnectionId, StreamQuality quality)
    {
        _hub = hub;
        _studentConnectionId = studentConnectionId;
        _quality = quality;
    }

    /// <summary>Wire up signalling handlers and ask the server to begin the stream.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        // The agent (sender) produces the offer; we answer it. Also take relayed ICE.
        _subscriptions.Add(_hub.On<SdpMessage>(Protocol.ClientMethods.ReceiveOffer, OnOffer));
        _subscriptions.Add(_hub.On<IceCandidateMessage>(Protocol.ClientMethods.ReceiveIceCandidate, OnIceCandidate));

        Status?.Invoke("Requesting stream…");
        await _hub.SendAsync(Protocol.ServerMethods.RequestStream,
            new StreamRequest { StudentConnectionId = _studentConnectionId, Quality = _quality }, ct);
    }

    private async Task OnOffer(SdpMessage offer)
    {
        // Only react to the offer from the student this receiver is watching.
        if (offer.FromConnectionId != _studentConnectionId) return;

        await _gate.WaitAsync();
        try
        {
            if (_disposed || _pc is not null) return;

            var pc = new RTCPeerConnection(null);

            // Receive-only VP8 video track that matches what the agent offers.
            pc.addTrack(new MediaStreamTrack(_decoder.SupportedFormats, MediaStreamStatusEnum.RecvOnly));

            // Reassembled VP8 focus frames can be large; give the depacketiser headroom.
            // MUST come after addTrack — the setter dereferences the video stream, which
            // doesn't exist until a track is added (verified: NRE otherwise).
            pc.MaxReconstructedVideoFrameSize = 2_000_000;

            pc.onicecandidate += candidate =>
            {
                if (candidate is null) return;
                _ = SafeSend(Protocol.ServerMethods.SendIceCandidate, new IceCandidateMessage
                {
                    TargetConnectionId = _studentConnectionId,
                    Candidate = candidate.ToString(),
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                });
            };

            pc.OnVideoFrameReceived += OnVideoFrame;

            pc.onconnectionstatechange += state =>
            {
                Status?.Invoke(state switch
                {
                    RTCPeerConnectionState.connected => "Streaming",
                    RTCPeerConnectionState.connecting => "Connecting…",
                    RTCPeerConnectionState.failed => "Connection failed.",
                    RTCPeerConnectionState.disconnected => "Disconnected.",
                    RTCPeerConnectionState.closed => "Closed.",
                    _ => state.ToString(),
                });
            };

            var set = pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = offer.Sdp,
            });
            if (set != SetDescriptionResultEnum.OK)
            {
                Status?.Invoke($"Could not accept offer: {set}");
                pc.close();
                return;
            }

            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);

            _pc = pc;

            await SafeSend(Protocol.ServerMethods.SendAnswer, new SdpMessage
            {
                TargetConnectionId = _studentConnectionId,
                Type = "answer",
                Sdp = answer.sdp,
            });
            Status?.Invoke("Negotiating…");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnIceCandidate(IceCandidateMessage message)
    {
        if (message.FromConnectionId != _studentConnectionId) return;
        _pc?.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = (ushort)(message.SdpMLineIndex ?? 0),
        });
    }

    private void OnVideoFrame(System.Net.IPEndPoint endPoint, uint timestamp, byte[] frame, VideoFormat format)
    {
        if (_disposed || FrameDecoded is null) return;

        // Decode the VP8 frame to BGR, then widen to BGRA for the canvas. DecodeVideo
        // yields nothing until the first keyframe arrives, which is expected.
        foreach (var sample in _decoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8))
        {
            var w = (int)sample.Width;
            var h = (int)sample.Height;
            // Clamp to a sane maximum before any sizing so a hostile/buggy sender can't
            // overflow the 32-bit size math (w*h*4) into a tiny/negative allocation and
            // drive an out-of-bounds write, or force a huge allocation. 8192² covers 8K.
            if (w is <= 0 or > 8192 || h is <= 0 or > 8192 || sample.Sample is null) continue;

            long pixels = (long)w * h;
            if (sample.Sample.Length < pixels * 3) continue;

            long need = pixels * 4;
            var dst = _bgra;
            if (dst is null || dst.Length < need) _bgra = dst = new byte[need];

            var src = sample.Sample;
            for (int i = 0, s = 0, d = 0; i < w * h; i++, s += 3, d += 4)
            {
                dst[d] = src[s];       // B
                dst[d + 1] = src[s + 1]; // G
                dst[d + 2] = src[s + 2]; // R
                dst[d + 3] = 0xFF;       // A
            }

            FrameDecoded?.Invoke(new DecodedFrame(w, h, dst));
        }
    }

    private async Task SafeSend(string method, object arg)
    {
        try
        {
            if (!_disposed && _hub.State == HubConnectionState.Connected)
                await _hub.SendAsync(method, arg);
        }
        catch
        {
            // Link dropping mid-negotiation is surfaced via connection state; ignore here.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var sub in _subscriptions) sub.Dispose();
        _subscriptions.Clear();

        try
        {
            if (_hub.State == HubConnectionState.Connected)
                await _hub.SendAsync(Protocol.ServerMethods.StopStream, _studentConnectionId);
        }
        catch { /* best effort */ }

        _pc?.close();
        _pc = null;
        _decoder.Dispose();
        _gate.Dispose();
    }
}

/// <summary>A single decoded video frame: 32-bpp BGRA, top-down, tightly packed (stride = Width*4).</summary>
public readonly record struct DecodedFrame(int Width, int Height, byte[] Bgra);
