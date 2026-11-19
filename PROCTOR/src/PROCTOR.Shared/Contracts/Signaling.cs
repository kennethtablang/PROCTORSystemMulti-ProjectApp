namespace PROCTOR.Shared.Contracts;

/// <summary>
/// WebRTC SDP exchange. The teacher (viewer) asks to watch a student; the student
/// (media sender) produces the offer, the teacher answers. The server only relays
/// these blobs between the two connection ids — it never parses the SDP.
/// </summary>
public sealed record SdpMessage
{
    /// <summary>Connection id of the peer this message is addressed to.</summary>
    public string TargetConnectionId { get; init; } = string.Empty;

    /// <summary>Connection id of the sender (filled in by the server on relay).</summary>
    public string FromConnectionId { get; init; } = string.Empty;

    /// <summary>"offer" or "answer".</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Raw SDP payload.</summary>
    public string Sdp { get; init; } = string.Empty;
}

/// <summary>A single ICE candidate relayed between peers during negotiation.</summary>
public sealed record IceCandidateMessage
{
    public string TargetConnectionId { get; init; } = string.Empty;
    public string FromConnectionId { get; init; } = string.Empty;

    public string Candidate { get; init; } = string.Empty;
    public string? SdpMid { get; init; }
    public int? SdpMLineIndex { get; init; }
}

/// <summary>Teacher → server request to begin viewing a student's screen.</summary>
public sealed record StreamRequest
{
    public string StudentConnectionId { get; init; } = string.Empty;

    /// <summary>
    /// Requested quality hint so the agent can scale encoding to how the tile is shown
    /// (thumbnail in the grid vs. a full-screen focused view).
    /// </summary>
    public StreamQuality Quality { get; init; } = StreamQuality.Thumbnail;

    /// <summary>
    /// Which of the student's displays to stream (index into <see cref="StudentInfo.Displays"/>;
    /// 0 = primary). Out-of-range values fall back to the primary display (FR-AGENT-04).
    /// </summary>
    public int DisplayIndex { get; init; }

    /// <summary>
    /// Requested stream width in pixels (0 = the default for <see cref="Quality"/>). The agent clamps
    /// it to <see cref="StreamResolution.MinWidth"/>..its IT-configured maximum and never upscales
    /// past the display; height follows the display's aspect ratio.
    /// </summary>
    public int MaxWidth { get; init; }
}

/// <summary>Stream resolution choices and limits shared by the teacher app and the agent.</summary>
public static class StreamResolution
{
    public const int MinWidth = 320;

    /// <summary>Default agent ceiling (<c>PROCTOR:MaxStreamWidth</c>); IT can raise it up to 3840.</summary>
    public const int DefaultMaxWidth = 1920;
    public const int AbsoluteMaxWidth = 3840;

    public const int DefaultThumbnailWidth = 480;
    /// <summary>Full HD: the sharpest focus stream that still holds ~15 fps on a typical lab PC.</summary>
    public const int DefaultFocusWidth = 1920;

    /// <summary>
    /// Thumbnail sizes offered in the teacher app. The bigger ones cost every student PC more CPU and
    /// the network more bandwidth (each tile is a separate stream), and the agent's
    /// <c>MaxStreamWidth</c> (1920 by default) still caps what's sent.
    /// </summary>
    public static readonly IReadOnlyList<(string Label, int Width)> ThumbnailChoices =
    [
        ("Small (320 px)", 320),
        ("Normal (480 px)", 480),
        ("Large (640 px)", 640),
        ("XL (960 px)", 960),
        ("XXL (1280 px)", 1280),
        ("XXXL (1920 px)", 1920),
    ];

    /// <summary>Focus-view resolutions offered in the teacher app.</summary>
    public static readonly IReadOnlyList<(string Label, int Width)> FocusChoices =
    [
        ("Low · 854 px", 854),
        ("Standard · 1280 px", 1280),
        ("High · 1600 px", 1600),
        ("Full HD · 1920 px", 1920),
        ("Native (up to 4K)", 3840),
    ];

    /// <summary>The width an agent should actually stream: requested (or default), clamped to its ceiling.</summary>
    public static int Effective(StreamQuality quality, int requested, int agentMax)
    {
        var want = requested > 0 ? requested
            : quality == StreamQuality.Focus ? DefaultFocusWidth : DefaultThumbnailWidth;
        var ceiling = Math.Clamp(agentMax <= 0 ? DefaultMaxWidth : agentMax, MinWidth, AbsoluteMaxWidth);
        return Math.Clamp(want, MinWidth, ceiling);
    }
}

/// <summary>
/// A STUN or TURN server handed to both WebRTC peers so a stream can still be set up across a
/// segmented network where direct LAN connectivity fails (relay fallback, P4). Configured on
/// the server under <c>PROCTOR:IceServers</c>; empty on a flat lab LAN.
/// </summary>
public sealed record IceServerConfig
{
    /// <summary>e.g. <c>stun:10.0.0.5:3478</c> or <c>turn:10.0.0.5:3478</c>.</summary>
    public string Urls { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Credential { get; init; }
}

public enum StreamQuality
{
    /// <summary>Low fps / downscaled — suitable for a grid of many tiles.</summary>
    Thumbnail = 0,
    /// <summary>Full resolution / higher fps — for a single focused student.</summary>
    Focus = 1,
}
