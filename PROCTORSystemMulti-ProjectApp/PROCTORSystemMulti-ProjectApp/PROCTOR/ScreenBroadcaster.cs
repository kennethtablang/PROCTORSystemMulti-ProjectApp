using System.Collections.Concurrent;
using System.Diagnostics;
using PROCTOR.Shared;
using PROCTOR.Shared.Contracts;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Shows the teacher's screen to the class (FR-CTRL-02). The teacher is the WebRTC sender: one peer
/// connection per student (offer from here, answer from the agent, ICE relayed only within the
/// broadcast), and each frame is captured and VP8-encoded <b>once</b> and sent to every connected
/// student — media is peer-to-peer and never touches the server (AD-4). Students who join
/// mid-broadcast get their own peer; a fresh keyframe is forced whenever someone connects.
/// </summary>
public sealed class ScreenBroadcaster : IAsyncDisposable
{
    private readonly TeacherClient _client;
    private readonly IScreenSource _ownScreen;
    private IScreenSource _source;
    private readonly ConcurrentDictionary<string, RTCPeerConnection> _peers = new();
    private readonly object _encodeGate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private VP8Codec? _encoder;
    private int _encW, _encH;
    private volatile bool _forceKey;

    /// <summary>Raised (any thread) when the broadcast starts/stops or the viewer count changes.</summary>
    public event Action? Changed;

    public ScreenBroadcaster(TeacherClient client, IScreenSource source)
    {
        _client = client;
        _ownScreen = source;
        _source = source;
        _client.BroadcastAnswerReceived += OnAnswer;
        _client.BroadcastIceReceived += OnIce;
        _client.BroadcastStudentJoined += id => { if (IsActive) _ = ConnectAsync(id); };
        // The server forgets the broadcast when our connection drops: stop locally too.
        _client.ConnectionLost += () => _ = StopLocalAsync();
        _client.Registered += () => { if (IsActive) _ = StopLocalAsync(); };
    }

    /// <summary>Whether this device can show its <b>own</b> screen (showing a student works everywhere).</summary>
    public bool IsSupported => _ownScreen.IsSupported;
    public bool IsActive { get; private set; }
    public BroadcastMode Mode { get; private set; }

    /// <summary>The student whose screen is being shown, or null when it's the teacher's own screen.</summary>
    public StudentTile? ShowingStudent { get; private set; }

    /// <summary>Students currently receiving the screen.</summary>
    public int Viewers => _peers.Values.Count(p => p.connectionState == RTCPeerConnectionState.connected);

    /// <summary>Start showing this screen. Returns an error to show, or null.</summary>
    public async Task<string?> StartAsync(BroadcastMode mode, IReadOnlyList<string>? targets = null, StudentTile? student = null)
    {
        if (student is null && !_ownScreen.IsSupported) return "Showing your screen isn't available on this device yet.";
        if (IsActive) await StopAsync();

        // Own screen: get the device ready first (Android asks the teacher to allow screen recording).
        if (student is null && await _ownScreen.PrepareAsync() is { } notReady) return notReady;

        IReadOnlyList<string> students;
        try
        {
            students = await _client.StartBroadcastAsync(new BroadcastRequest
            {
                Mode = mode,
                Targets = targets ?? [],
                SourceStudentConnectionId = student?.ConnectionId,
            });
        }
        catch (Exception ex)
        {
            if (student is null) _ownScreen.Release();
            return ex.Message;
        }

        _source = student is null ? _ownScreen : new StudentFrameSource(student);
        ShowingStudent = student;
        IsActive = true;
        Mode = mode;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => CaptureLoopAsync(_cts.Token));
        foreach (var s in students) _ = ConnectAsync(s);
        Changed?.Invoke();
        return null;
    }

    public async Task StopAsync()
    {
        if (!IsActive) return;
        try { await _client.StopBroadcastAsync(); } catch { }
        await StopLocalAsync();
    }

    private async Task StopLocalAsync()
    {
        if (!IsActive) return;
        IsActive = false;
        _cts?.Cancel();
        if (_loop is not null) { try { await _loop; } catch { } }
        _loop = null;
        foreach (var id in _peers.Keys.ToArray())
            if (_peers.TryRemove(id, out var pc)) { try { pc.close(); } catch { } }
        lock (_encodeGate) { _encoder?.Dispose(); _encoder = null; _encW = _encH = 0; }
        if (ReferenceEquals(_source, _ownScreen)) _ownScreen.Release();
        ShowingStudent = null;
        _source = _ownScreen;
        Changed?.Invoke();
    }

    private async Task ConnectAsync(string student)
    {
        if (_peers.TryRemove(student, out var old)) { try { old.close(); } catch { } }

        var pc = new RTCPeerConnection(ScreenStreamReceiver.BuildConfig(_client.IceServers));
        using (var formats = new VP8Codec())
            pc.addTrack(new MediaStreamTrack(formats.SupportedFormats, MediaStreamStatusEnum.SendOnly));

        pc.onicecandidate += c =>
        {
            if (c is null) return;
            _ = _client.SendAsync(Protocol.ServerMethods.SendBroadcastIce, new IceCandidateMessage
            {
                TargetConnectionId = student,
                Candidate = c.ToString(),
                SdpMid = c.sdpMid,
                SdpMLineIndex = c.sdpMLineIndex,
            });
        };
        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected) _forceKey = true;   // new viewer needs a keyframe
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                _peers.TryRemove(new KeyValuePair<string, RTCPeerConnection>(student, pc));
            Changed?.Invoke();
        };
        _peers[student] = pc;

        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        await _client.SendAsync(Protocol.ServerMethods.SendBroadcastOffer, new SdpMessage
        {
            TargetConnectionId = student,
            Type = "offer",
            Sdp = offer.sdp,
        });
    }

    private void OnAnswer(SdpMessage answer)
    {
        if (_peers.TryGetValue(answer.FromConnectionId, out var pc))
            pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer.Sdp });
    }

    private void OnIce(IceCandidateMessage c)
    {
        if (_peers.TryGetValue(c.FromConnectionId, out var pc))
            pc.addIceCandidate(new RTCIceCandidateInit { candidate = c.Candidate, sdpMid = c.SdpMid, sdpMLineIndex = (ushort)(c.SdpMLineIndex ?? 0) });
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / BroadcastLimits.Fps);
        var clock = Stopwatch.StartNew();
        var due = TimeSpan.Zero;
        TimeSpan? lastSent = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var connected = _peers.Values.Where(p => p.connectionState == RTCPeerConnectionState.connected).ToArray();
                if (connected.Length > 0 && _source.Capture(BroadcastLimits.Width) is { } f)
                {
                    var now = clock.Elapsed;
                    var send = _forceKey || f.Changed || lastSent is null || now - lastSent.Value >= TimeSpan.FromSeconds(1);
                    if (send)
                    {
                        byte[] encoded;
                        lock (_encodeGate)
                        {
                            if (_encoder is null || _encW != f.Width || _encH != f.Height)
                            {
                                _encoder?.Dispose();
                                _encoder = new VP8Codec();   // a fresh codec starts with a keyframe
                                _encW = f.Width; _encH = f.Height;
                                _forceKey = false;
                            }
                            if (_forceKey) { _encoder.ForceKeyFrame(); _forceKey = false; }
                            encoded = _encoder.EncodeVideo(f.Width, f.Height, f.Bgra, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);
                        }
                        if (encoded is { Length: > 0 })
                        {
                            var duration = (uint)Math.Max(1, ((lastSent is null ? interval : now - lastSent.Value).TotalSeconds) * 90000);
                            foreach (var pc in connected)
                            {
                                try { pc.SendVideo(duration, encoded); } catch { }
                            }
                            lastSent = now;
                        }
                    }
                }
            }
            catch
            {
                // A bad frame shouldn't end the broadcast.
            }

            due += interval;
            if (due < clock.Elapsed - interval) due = clock.Elapsed;
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
