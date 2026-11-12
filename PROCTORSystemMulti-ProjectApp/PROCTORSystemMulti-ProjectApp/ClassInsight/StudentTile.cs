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

    private string? _foreground;
    public string ForegroundText => string.IsNullOrWhiteSpace(_foreground) ? "" : _foreground!;

    public void Apply(TelemetrySnapshot t)
    {
        CpuPercent = t.System.CpuPercent;
        MemoryPercent = t.System.MemoryPercent;
        _down = t.Bandwidth.DownloadBytesPerSec;
        _up = t.Bandwidth.UploadBytesPerSec;
        _foreground = t.ForegroundWindowTitle;
        Raise(nameof(BandwidthText));
        Raise(nameof(ForegroundText));
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
