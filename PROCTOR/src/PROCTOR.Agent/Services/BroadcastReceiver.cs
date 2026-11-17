using System.Runtime.Versioning;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

namespace PROCTOR.Agent.Services;

/// <summary>
/// The student side of a screen broadcast (FR-CTRL-02): answers the broadcasting teacher's offer,
/// exchanges ICE through the hub (relayed only within the broadcast), and decodes the teacher's
/// VP8 screen into the student UI's viewer window. Media is peer-to-peer and DTLS-SRTP encrypted.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BroadcastReceiver : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly string _teacher;
    private readonly ClassroomUi _ui;
    private readonly IReadOnlyList<IceServerConfig> _iceServers;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private RTCPeerConnection? _pc;
    private VP8Codec? _decoder;
    private volatile bool _disposed;

    public BroadcastReceiver(HubConnection hub, string teacher, ClassroomUi ui, IReadOnlyList<IceServerConfig> iceServers, ILogger logger)
    {
        _hub = hub;
        _teacher = teacher;
        _ui = ui;
        _iceServers = iceServers;
        _logger = logger;
    }

    public string Teacher => _teacher;

    public async Task HandleOfferAsync(SdpMessage offer)
    {
        if (_disposed || offer.FromConnectionId != _teacher) return;

        RTCPeerConnection pc;
        RTCSessionDescriptionInit answer;
        lock (_gate)
        {
            try { _pc?.close(); } catch { }
            _decoder?.Dispose();

            var decoder = new VP8Codec();
            pc = new RTCPeerConnection(ScreenStreamSender.BuildConfig(_iceServers));
            pc.addTrack(new MediaStreamTrack(decoder.SupportedFormats, MediaStreamStatusEnum.RecvOnly));
            pc.MaxReconstructedVideoFrameSize = 3_000_000;   // after addTrack (setter needs the video stream)

            pc.onicecandidate += c =>
            {
                if (c is null) return;
                _ = Send(Protocol.ServerMethods.SendBroadcastIce, new IceCandidateMessage
                {
                    TargetConnectionId = _teacher,
                    Candidate = c.ToString(),
                    SdpMid = c.sdpMid,
                    SdpMLineIndex = c.sdpMLineIndex,
                });
            };
            pc.OnVideoFrameReceived += (_, _, frame, _) => OnFrame(pc, decoder, frame);
            pc.onconnectionstatechange += s => _logger.LogDebug("Broadcast from {Teacher}: {State}", _teacher, s);

            if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer.Sdp }) != SetDescriptionResultEnum.OK)
            {
                _logger.LogWarning("Rejected the teacher's broadcast offer.");
                pc.close();
                decoder.Dispose();
                return;
            }
            answer = pc.createAnswer();
            _pc = pc;
            _decoder = decoder;
        }

        await pc.setLocalDescription(answer);
        await Send(Protocol.ServerMethods.SendBroadcastAnswer, new SdpMessage { TargetConnectionId = _teacher, Type = "answer", Sdp = answer.sdp });
    }

    public void HandleIce(IceCandidateMessage message)
    {
        if (message.FromConnectionId != _teacher) return;
        RTCPeerConnection? pc;
        lock (_gate) pc = _pc;
        pc?.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = (ushort)(message.SdpMLineIndex ?? 0),
        });
    }

    private void OnFrame(RTCPeerConnection source, VP8Codec decoder, byte[] frame)
    {
        if (_disposed || !ReferenceEquals(source, _pc)) return;
        if (!KeyframeWithinCap(frame)) return;   // refuse oversized streams before decoding
        try
        {
            foreach (var s in decoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8))
            {
                int w = (int)s.Width, h = (int)s.Height;
                if (w <= 0 || h <= 0 || w > BroadcastLimits.MaxFrameWidth || h > BroadcastLimits.MaxFrameHeight || s.Sample is null) continue;
                if (s.Sample.Length < (long)w * h * 3) continue;
                _ui.ShowBroadcastFrame(w, h, s.Sample);
            }
        }
        catch
        {
            // A corrupt frame shouldn't end the broadcast; the next keyframe recovers it.
        }
    }

    // VP8 keyframe header (RFC 6386 §9.1): start code 9D 01 2A then 14-bit width/height.
    private static bool KeyframeWithinCap(byte[] f)
    {
        if (f.Length < 10 || (f[0] & 0x01) != 0) return true;
        if (f[3] != 0x9D || f[4] != 0x01 || f[5] != 0x2A) return false;
        var w = (f[6] | (f[7] << 8)) & 0x3FFF;
        var h = (f[8] | (f[9] << 8)) & 0x3FFF;
        return w > 0 && h > 0 && w <= BroadcastLimits.MaxFrameWidth && h <= BroadcastLimits.MaxFrameHeight;
    }

    private async Task Send(string method, object arg)
    {
        try
        {
            if (!_disposed && _hub.State == HubConnectionState.Connected) await _hub.SendAsync(method, arg);
        }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_gate)
        {
            try { _pc?.close(); } catch { }
            _pc = null;
            var d = _decoder;
            _decoder = null;
            if (d is not null) _ = Task.Delay(500).ContinueWith(_ => d.Dispose());
        }
        return ValueTask.CompletedTask;
    }
}
