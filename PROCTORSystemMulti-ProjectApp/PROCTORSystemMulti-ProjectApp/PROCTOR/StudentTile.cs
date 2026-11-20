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
        IpAddress = snap.IpAddress ?? "";
        MacAddresses = snap.MacAddresses ?? [];
        Displays = snap.Displays.Count > 0
            ? snap.Displays
            : [new DisplayInfo { Index = 0, Width = snap.ScreenWidth, Height = snap.ScreenHeight, IsPrimary = true }];
        _state = snap.State;
        _isLocked = snap.IsLocked;
        _handRaised = snap.HandRaised;
        _ownWebLimits = snap.OwnWebLimits;
        _studentName = snap.StudentName;
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
    public string IpAddress { get; }
    public IReadOnlyList<string> MacAddresses { get; }

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
    /// green while being moved in the lab layout, red while it has an alert, purple when selected,
    /// amber while the hand is up.
    /// </summary>
    public Color TileBorderColor =>
        _isSwapSource ? Color.FromArgb("#66BB6A")
        : _alertText is not null ? Color.FromArgb("#EF5350")
        : _isSelected ? Color.FromArgb("#B794F6")
        : _handRaised ? Color.FromArgb("#F9A825")
        : Colors.Transparent;

    // ---- teacher alerts ----

    private string? _alertText;
    /// <summary>The latest alert for this PC not yet dismissed (e.g. "Blocked site: youtube.com"), or null.</summary>
    public string? AlertText
    {
        get => _alertText;
        set
        {
            if (_alertText == value) return;
            _alertText = value;
            Raise(); Raise(nameof(HasAlert)); Raise(nameof(TileBorderColor));
        }
    }
    public bool HasAlert => _alertText is not null;

    // ---- idle (no keyboard or mouse) ----

    private int? _idleSeconds;
    /// <summary>Seconds since the student last used the keyboard or mouse; null from older agents.</summary>
    public int? IdleSeconds => _idleSeconds;

    /// <summary>"Idle 6 min" once nobody has touched the PC for a minute or more; empty otherwise.</summary>
    public string IdleText => _idleSeconds is { } s && s >= 60 ? $"Idle {FormatDuration(s)}" : "";
    public bool IsIdle => _idleSeconds is >= 60;

    private void SetIdle(int? seconds)
    {
        var before = IdleText;
        _idleSeconds = seconds;
        if (IdleText != before) { Raise(nameof(IdleText)); Raise(nameof(IsIdle)); }
    }

    public static string FormatDuration(int seconds) =>
        seconds < 3600 ? $"{seconds / 60} min" : seconds < 86400 ? $"{seconds / 3600} h {seconds / 60 % 60} min" : $"{seconds / 86400} d {seconds / 3600 % 24} h";

    // ---- PC inventory ----

    private PcInventory? _inventory;
    /// <summary>The PC's last hardware / Windows report (arrives within 30 s of joining); null until then.</summary>
    public PcInventory? Inventory
    {
        get => _inventory;
        private set { _inventory = value; Raise(); }
    }

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

    /// <summary>"PC05 · Dela Cruz, Juan M." once the student gave their name; else the Windows user.</summary>
    public string Header => _studentName is { } n ? $"{MachineName} · {n.Display}"
        : string.IsNullOrWhiteSpace(UserName) ? MachineName : $"{MachineName} · {UserName}";

    private StudentName? _studentName;
    /// <summary>The name the student typed on the attendance form, or null.</summary>
    public StudentName? StudentName
    {
        get => _studentName;
        set
        {
            _studentName = value;
            Raise(); Raise(nameof(Header)); Raise(nameof(HasStudentName)); Raise(nameof(TooltipText));
        }
    }
    public bool HasStudentName => _studentName is not null;

    /// <summary>Everything about this computer, shown when the mouse rests on its screen.</summary>
    public string TooltipText
    {
        get
        {
            var lines = new List<string> { MachineName };
            lines.Add(_studentName is { } n
                ? $"Student: {n.FullName} (name given {n.SubmittedAt.ToLocalTime():HH:mm})"
                : "Student: no name given yet");
            if (!string.IsNullOrWhiteSpace(UserName)) lines.Add($"Windows user: {UserName}");
            var net = string.Join("  ·  ", new[] { string.IsNullOrEmpty(IpAddress) ? null : "IP " + IpAddress,
                MacAddresses.Count > 0 ? "MAC " + string.Join(", ", MacAddresses) : null }.Where(x => x is not null));
            if (net.Length > 0) lines.Add(net);
            if (_inventory is { } i)
            {
                var hw = string.Join(" · ", new[] { string.Join(" ", new[] { i.Manufacturer, i.Model }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    i.CpuName, i.MemoryTotalBytes > 0 ? Human(i.MemoryTotalBytes) + " RAM" : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (hw.Length > 0) lines.Add(hw);
                var os = i.Windows ?? "";
                if (i.DiskTotalBytes > 0) os += (os.Length > 0 ? " · " : "") + $"{Human(i.DiskFreeBytes)} free of {Human(i.DiskTotalBytes)}";
                if (os.Length > 0) lines.Add(os);
                if (i.AgentVersion is { } v) lines.Add($"PROCTOR agent {v}");
            }
            lines.Add($"{StateText} · CPU {_cpu:0}% · RAM {_mem:0}% · {BandwidthText}");
            if (!string.IsNullOrWhiteSpace(_foreground)) lines.Add("Now: " + (_foreground!.Length > 80 ? _foreground[..80] + "…" : _foreground));
            if (!string.IsNullOrWhiteSpace(_activeHost)) lines.Add("Website: " + _activeHost);
            if (IsIdle) lines.Add(IdleText);
            if (HasUsbDrive) lines.Add(UsbText);
            if (_isLocked) lines.Add("Screen locked");
            if (_handRaised) lines.Add("Needs help (hand raised)");
            return string.Join(Environment.NewLine, lines);
        }
    }

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

    // ---- flash drives -----------------------------------------------------------------------

    private IReadOnlyList<RemovableDrive> _drives = [];

    /// <summary>A USB flash drive or memory card is plugged into this PC.</summary>
    public bool HasUsbDrive => _drives.Count > 0;

    /// <summary>"Flash drive: E: KINGSTON (29.8 GB)" — shown on hover / in the student view.</summary>
    public string UsbText => _drives.Count switch
    {
        0 => "",
        1 => "Flash drive: " + Describe(_drives[0]),
        _ => $"{_drives.Count} flash drives: " + string.Join(", ", _drives.Select(Describe)),
    };

    /// <summary>Raised (UI thread) when a flash drive is plugged in where there was none.</summary>
    public event Action<StudentTile>? UsbInserted;

    private void SetDrives(IReadOnlyList<RemovableDrive> drives)
    {
        var before = string.Join("|", _drives.Select(d => d.Letter + d.Label + d.SizeBytes));
        var after = string.Join("|", drives.Select(d => d.Letter + d.Label + d.SizeBytes));
        if (before == after) return;
        var wasEmpty = _drives.Count == 0;
        _drives = drives.Take(RemovableDrive.MaxReported).ToArray();
        Raise(nameof(HasUsbDrive));
        Raise(nameof(UsbText));
        if (wasEmpty && _drives.Count > 0) UsbInserted?.Invoke(this);
    }

    private static string Describe(RemovableDrive d)
    {
        var label = d.Label is { Length: > 0 } l ? " " + (l.Length > 32 ? l[..32] : l) : "";
        return $"{d.Letter}{label} ({Human(d.SizeBytes)})";
    }

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
        SetDrives(t.RemovableDrives ?? []);
        SetIdle(t.IdleSeconds);
        if (t.Inventory is { } inventory) Inventory = inventory;

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
        Raise(nameof(TooltipText));
    }

    public static string Human(double bytes)
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
