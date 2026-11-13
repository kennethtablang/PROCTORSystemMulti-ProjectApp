using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClassInsight.Shared.Contracts;
using Microsoft.Maui.Graphics;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// One student tile in the dashboard grid. Holds live telemetry and state for a single
/// connected student and raises change notifications so the bound UI updates in place.
/// </summary>
public sealed class StudentTile : INotifyPropertyChanged
{
    public StudentTile(StudentSnapshot snap)
    {
        ConnectionId = snap.ConnectionId;
        MachineName = string.IsNullOrWhiteSpace(snap.MachineName) ? "(unknown)" : snap.MachineName;
        UserName = snap.UserName;
        _state = snap.State;
        if (snap.LastTelemetry is { } t) Apply(t);
    }

    public string ConnectionId { get; }
    public string MachineName { get; }
    public string? UserName { get; }

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

    /// <summary>Session-total bytes, shown in the focus detail panel (FR-TEL-05 groundwork).</summary>
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
