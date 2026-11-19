using System.ComponentModel;
using System.Runtime.CompilerServices;
using PROCTOR.Shared.Contracts;
using DisplayInfo = PROCTOR.Shared.Contracts.DisplayInfo;
using Microsoft.Maui.Graphics;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// One student tile in the dashboard grid. Holds live telemetry, state and the latest video
/// frame for a single connected student and raises change notifications so the bound UI updates
/// in place. Change notifications are raised on the UI thread (the view model marshals telemetry
/// there); <see cref="Frame"/> is written from the decode thread and has its own event.
/// </summary>
public sealed class StudentTile : INotifyPropertyChanged
{
    public StudentTile(StudentSnapshot snap)
    {
        ConnectionId = snap.ConnectionId;
        MachineName = string.IsNullOrWhiteSpace(snap.MachineName) ? "(unknown)" : snap.MachineName;
        UserName = snap.UserName;
        Displays = snap.Displays.Count > 0
            ? snap.Displays
            : [new DisplayInfo { Index = 0, Width = snap.ScreenWidth, Height = snap.ScreenHeight, IsPrimary = true }];
        _state = snap.State;
        _isLocked = snap.IsLocked;
        _handRaised = snap.HandRaised;
        _ownWebLimits = snap.OwnWebLimits;
        if (snap.LastTelemetry is { } t) Apply(t);

        // Flip HasVideo when the first frame lands / the stream is cleared. Updated fires per
        // frame on the decode thread, so only dispatch to the UI thread on an actual change.
        Frame.Updated += () =>
        {
            var has = Frame.HasFrame;
            if (has == _hasVideoSeen) return;
            _hasVideoSeen = has;
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() => HasVideo = has);
        };
    }

    private volatile bool _hasVideoSeen;

    public string ConnectionId { get; }
    public string MachineName { get; }
    public string? UserName { get; }

    /// <summary>The student's displays, primary first (FR-AGENT-04).</summary>
    public IReadOnlyList<DisplayInfo> Displays { get; }
    public bool HasMultipleDisplays => Displays.Count > 1;

    /// <summary>Picker labels for <see cref="Displays"/>.</summary>
    public IReadOnlyList<string> DisplayLabels => Displays
        .Select(d => $"Display {d.Index + 1}{(d.IsPrimary ? " (primary)" : "")} · {d.Width}×{d.Height}")
        .ToArray();

    private bool _isLocked;
    /// <summary>The student's screen is locked by a teacher (FR-CTRL-01).</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set { if (_isLocked != value) { _isLocked = value; Raise(); Raise(nameof(LockText)); } }
    }
    public string LockText => _isLocked ? "Locked" : "";

    private WebLimits? _ownWebLimits;
    /// <summary>This PC's own website list (replacing the class's), or null when it follows the class.</summary>
    public WebLimits? OwnWebLimits
    {
        get => _ownWebLimits;
        set { _ownWebLimits = value; Raise(); Raise(nameof(HasOwnWebLimits)); Raise(nameof(OwnWebLimitsText)); }
    }

    public bool HasOwnWebLimits => _ownWebLimits is not null;

    public string OwnWebLimitsText => _ownWebLimits switch
    {
        null => "",
        { WebMode: WebLimitMode.AllowOnlyListed } w => $"Own websites: only {w.WebHosts.Count} allowed",
        { WebMode: WebLimitMode.BlockListed } w => $"Own websites: {w.WebHosts.Count} blocked",
        _ => "Own websites: no limits",
    };

    private bool _handRaised;
    /// <summary>The student has raised their hand asking for help.</summary>
    public bool HandRaised
    {
        get => _handRaised;
        set { if (_handRaised != value) { _handRaised = value; Raise(); Raise(nameof(TileBorderColor)); } }
    }

    private bool _isSelected;
    /// <summary>Ticked on the dashboard: the toolbar's actions apply to the selected students only.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(); Raise(nameof(TileBorderColor)); } }
    }

    private bool _isSwapSource;
    /// <summary>Lab layout, rearranging: this PC was tapped first and moves to the next seat tapped.</summary>
    public bool IsSwapSource
    {
        get => _isSwapSource;
        set { if (_isSwapSource != value) { _isSwapSource = value; Raise(); Raise(nameof(TileBorderColor)); } }
    }

    /// <summary>Seat key in the lab layout: the computer name, case-insensitive.</summary>
    public string SeatKey => MachineName.ToUpperInvariant();

    private int _unreadChat;
    /// <summary>Chat lines from this student the teacher hasn't read yet.</summary>
    public int UnreadChat
    {
        get => _unreadChat;
        set { if (_unreadChat != value) { _unreadChat = value; Raise(); Raise(nameof(ChatBadge)); Raise(nameof(HasUnreadChat)); } }
    }
    public bool HasUnreadChat => _unreadChat > 0;
    public string ChatBadge => _unreadChat > 0 ? _unreadChat.ToString() : "";

    /// <summary>
    /// Tile outline: none normally (a light outline read as a white line around every screen);
    /// green while being moved in the lab layout, purple when selected, amber while the hand is up.
    /// </summary>
    public Color TileBorderColor =>
        _isSwapSource ? Color.FromArgb("#66BB6A")
        : _isSelected ? Color.FromArgb("#B794F6")
        : _handRaised ? Color.FromArgb("#F9A825")
        : Colors.Transparent;

    /// <summary>Latest decoded screen frame — what the thumbnail and the focus view paint.</summary>
    public FrameBuffer Frame { get; } = new();

    private string _streamStatus = "";
    /// <summary>Viewer-side stream status ("Connecting…", "Live", "Connection failed."…).</summary>
    public string StreamStatus
    {
        get => _streamStatus;
        set { if (_streamStatus != value) { _streamStatus = value; Raise(); Raise(nameof(PlaceholderText)); } }
    }

    private bool _hasVideo;
    /// <summary>True once a frame has arrived for the current stream; drives the placeholder overlay.</summary>
    public bool HasVideo
    {
        get => _hasVideo;
        private set { if (_hasVideo != value) { _hasVideo = value; Raise(); Raise(nameof(PlaceholderText)); } }
    }

    public string PlaceholderText => HasVideo ? "" : string.IsNullOrEmpty(_streamStatus) ? "Tap to view live screen" : _streamStatus;

    /// <summary>Live upload+download rate, used for sorting by bandwidth (FR-TEACH-06).</summary>
    public double TotalRate => _down + _up;

    public string Header => string.IsNullOrWhiteSpace(UserName) ? MachineName : $"{MachineName} · {UserName}";

    private StudentState _state;
    public StudentState State
    {
        get => _state;
        set { if (_state != value) { _state = value; Raise(); Raise(nameof(StateText)); Raise(nameof(StateColor)); } }
    }

    public string StateText => _state switch
    {
        StudentState.Streaming => "Streaming",
        StudentState.Idle => "Idle",
        StudentState.Offline => "Offline",
        _ => "Online",
    };

    public Color StateColor => _state switch
    {
        StudentState.Streaming => Color.FromArgb("#2E7D32"), // green
        StudentState.Idle => Color.FromArgb("#F9A825"),      // amber
        StudentState.Offline => Color.FromArgb("#9E9E9E"),   // grey
        _ => Color.FromArgb("#1565C0"),                       // blue
    };

    private double _cpu;
    public double CpuPercent { get => _cpu; private set { _cpu = value; Raise(); Raise(nameof(CpuText)); } }
    public string CpuText => $"CPU {_cpu:0}%";

    private double _mem;
    public double MemoryPercent { get => _mem; private set { _mem = value; Raise(); Raise(nameof(MemoryText)); } }
    public string MemoryText => $"RAM {_mem:0}%";

    private double _down, _up;
    public string BandwidthText => $"↓ {Human(_down)}/s  ↑ {Human(_up)}/s";

    /// <summary>Split rate labels for the bandwidth overview (FR-TEACH-03 / FR-TEL-03).</summary>
    public string DownloadRateText => $"↓ {Human(_down)}/s";
    public string UploadRateText => $"↑ {Human(_up)}/s";

    // Relative activity bars for the bandwidth view. The cap is a soft reference point (1 MB/s),
    // not a hard limit — it just keeps the bars readable; sustained high traffic pins at full.
    private const double BarCapBytesPerSec = 1_000_000.0;
    public double DownloadBarFraction => Math.Min(1.0, _down / BarCapBytesPerSec);
    public double UploadBarFraction => Math.Min(1.0, _up / BarCapBytesPerSec);

    private string? _foreground;
    public string ForegroundText => string.IsNullOrWhiteSpace(_foreground) ? "" : _foreground!;

    private string? _activeHost;
    /// <summary>Host of the foreground browser tab, if the agent reports it (FR-AGENT-06).</summary>
    public string ActiveHostText => string.IsNullOrWhiteSpace(_activeHost) ? "" : _activeHost!;
    public bool HasActiveHost => !string.IsNullOrWhiteSpace(_activeHost);

    /// <summary>Session-total bytes, shown in the focus detail panel and bandwidth view (FR-TEL-05).</summary>
    public string TotalsText => $"Session ↓ {Human(_totalDown)}  ↑ {Human(_totalUp)}";
    private long _totalDown, _totalUp;

    /// <summary>Top processes by resource use, refreshed live for the detail panel (FR-TEL-03).</summary>
    public IReadOnlyList<ProcessRow> Processes { get; private set; } = [];

    public void Apply(TelemetrySnapshot t)
    {
        CpuPercent = t.System.CpuPercent;
        MemoryPercent = t.System.MemoryPercent;
        _down = t.Bandwidth.DownloadBytesPerSec;
        _up = t.Bandwidth.UploadBytesPerSec;
        _totalDown = t.Bandwidth.TotalDownloadBytes;
        _totalUp = t.Bandwidth.TotalUploadBytes;
        _foreground = t.ForegroundWindowTitle;
        _activeHost = t.ActiveUrl;

        Processes = t.TopProcesses
            .Select(p => new ProcessRow(p.Name, $"PID {p.Pid}", $"{p.CpuPercent:0.#}%", Human(p.MemoryBytes)))
            .ToArray();

        Raise(nameof(BandwidthText));
        Raise(nameof(DownloadRateText));
        Raise(nameof(UploadRateText));
        Raise(nameof(DownloadBarFraction));
        Raise(nameof(UploadBarFraction));
        Raise(nameof(TotalsText));
        Raise(nameof(ForegroundText));
        Raise(nameof(ActiveHostText));
        Raise(nameof(HasActiveHost));
        Raise(nameof(TotalRate));
        Raise(nameof(Processes));
    }

    private static string Human(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One row in a student's live process list (FR-TEL-03), pre-formatted for display.</summary>
public sealed record ProcessRow(string Name, string Pid, string Cpu, string Mem);
