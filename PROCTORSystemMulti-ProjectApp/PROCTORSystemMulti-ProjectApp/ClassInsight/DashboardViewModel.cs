using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClassInsight.Shared.Contracts;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// Backs the dashboard: owns the <see cref="TeacherClient"/>, keeps the live roster as
/// an observable collection of <see cref="StudentTile"/>, and marshals every server
/// event onto the UI thread.
/// </summary>
public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly TeacherClient _client = new();
    private readonly Dictionary<string, StudentTile> _byConnection = new();

    public ObservableCollection<StudentTile> Students { get; } = new();

    public DashboardViewModel()
    {
        _client.Status += s => OnUi(() => Status = s);
        _client.RosterUpdated += r => OnUi(() => ReplaceRoster(r));
        _client.StudentJoined += s => OnUi(() => AddOrUpdate(s));
        _client.StudentLeft += id => OnUi(() => Remove(id));
        _client.StudentStateChanged += (id, st) => OnUi(() => SetState(id, st));
        _client.TelemetryUpdated += (id, t) => OnUi(() => ApplyTelemetry(id, t));

        ConnectCommand = new Command(async () => await ConnectAsync(), () => !IsBusy && !IsConnected);
    }

    private string _joinCode = string.Empty;
    public string JoinCode { get => _joinCode; set { _joinCode = value; Raise(); } }

    private string _status = "Enter the join code shown on the server console, then Connect.";
    public string Status { get => _status; private set { _status = value; Raise(); } }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { _isConnected = value; Raise(); Raise(nameof(NotConnected)); RefreshCommand(); }
    }
    public bool NotConnected => !_isConnected;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Raise(); RefreshCommand(); } }

    public int StudentCount => Students.Count;
    public bool HasStudents => Students.Count > 0;

    public ICommand ConnectCommand { get; }

    public async Task ConnectAsync()
    {
        if (IsBusy || IsConnected) return;
        IsBusy = true;
        try
        {
            var ok = await _client.ConnectAsync(
                JoinCode?.Trim() ?? string.Empty,
                DeviceInfo.Name ?? "Teacher",
                DeviceInfo.Platform.ToString(),
                CancellationToken.None);
            IsConnected = ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReplaceRoster(RosterSnapshot roster)
    {
        Students.Clear();
        _byConnection.Clear();
        foreach (var s in roster.Students) AddOrUpdate(s);
    }

    private void AddOrUpdate(StudentSnapshot snap)
    {
        if (_byConnection.TryGetValue(snap.ConnectionId, out var existing))
        {
            existing.State = snap.State;
            return;
        }
        var tile = new StudentTile(snap);
        _byConnection[snap.ConnectionId] = tile;
        Students.Add(tile);
        RaiseCounts();
    }

    private void Remove(string connectionId)
    {
        if (_byConnection.Remove(connectionId, out var tile))
        {
            Students.Remove(tile);
            RaiseCounts();
        }
    }

    private void SetState(string connectionId, StudentState state)
    {
        if (_byConnection.TryGetValue(connectionId, out var tile)) tile.State = state;
    }

    private void ApplyTelemetry(string connectionId, TelemetrySnapshot telemetry)
    {
        if (_byConnection.TryGetValue(connectionId, out var tile)) tile.Apply(telemetry);
    }

    private void RaiseCounts()
    {
        Raise(nameof(StudentCount));
        Raise(nameof(HasStudents));
    }

    private void RefreshCommand() => (ConnectCommand as Command)?.ChangeCanExecute();

    private static void OnUi(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
