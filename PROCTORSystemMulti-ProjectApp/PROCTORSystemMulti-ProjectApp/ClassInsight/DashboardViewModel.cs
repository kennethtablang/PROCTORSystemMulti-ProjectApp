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
    private readonly CancellationTokenSource _cts = new();
    private bool _maintaining;

    public ObservableCollection<StudentTile> Students { get; } = new();

    /// <summary>The shared server link, handed to the focus view so its WebRTC stream
    /// rides the same signalling connection.</summary>
    public TeacherClient Client => _client;

    public DashboardViewModel()
    {
        _client.Status += s => OnUi(() => Status = s);
        _client.RosterUpdated += r => OnUi(() => ReplaceRoster(r));
        _client.StudentJoined += s => OnUi(() => AddOrUpdate(s));
        _client.StudentLeft += id => OnUi(() => Remove(id));
        _client.StudentStateChanged += (id, st) => OnUi(() => SetState(id, st));
        _client.TelemetryUpdated += (id, t) => OnUi(() => ApplyTelemetry(id, t));
        // The connection dropped for good (server restart / network loss): drop our roster and
        // start re-discovering so we latch back on automatically (FR-DISC-05).
        _client.ConnectionLost += () => OnUi(OnConnectionLost);

        ConnectCommand = new Command(async () => await ConnectAsync(), () => !IsBusy && !IsConnected);
    }

    private string _joinCode = string.Empty;
    public string JoinCode { get => _joinCode; set { _joinCode = value; Raise(); } }

    private string _status = "Looking for the classroom server…";
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

    /// <summary>
    /// False by default so the app "just opens and connects". It flips true only when a
    /// server is found but rejects the no-code attempt (i.e. the room is running with a
    /// teacher code), at which point the UI reveals the code box for manual entry.
    /// </summary>
    private bool _needsJoinCode;
    public bool NeedsJoinCode { get => _needsJoinCode; private set { _needsJoinCode = value; Raise(); } }

    public int StudentCount => Students.Count;
    public bool HasStudents => Students.Count > 0;

    public ICommand ConnectCommand { get; }

    private bool _autoTried;

    /// <summary>
    /// Called once when the dashboard appears: try to connect with no code. On an open
    /// server this lands straight on the live grid; only if the server asks for a code do
    /// we show the box (NeedsJoinCode) so the teacher can type it.
    /// </summary>
    public Task AutoConnectAsync()
    {
        if (_autoTried) return Task.CompletedTask;
        _autoTried = true;
        return MaintainConnectionAsync();
    }

    /// <summary>
    /// A single, user-initiated connect attempt — used by the Connect button once the server has
    /// asked for a join code. Automatic retry/reconnect is handled by <see cref="MaintainConnectionAsync"/>.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (IsBusy || IsConnected) return;
        IsBusy = true;
        try
        {
            var outcome = await _client.ConnectAsync(
                JoinCode?.Trim() ?? string.Empty,
                DeviceInfo.Name ?? "Teacher",
                DeviceInfo.Platform.ToString(),
                _cts.Token);

            IsConnected = outcome == ConnectOutcome.Connected;
            // Reveal the code box only when the server actually requires one.
            NeedsJoinCode = outcome == ConnectOutcome.NeedsJoinCode;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Keeps trying to reach a classroom server until connected. Handles "the server isn't up yet"
    /// at launch and full re-discovery after a drop (FR-DISC-05): it retries every few seconds on
    /// NoServer/Error, and stops only once connected or when the server is waiting on the teacher
    /// (needs a join code / incompatible version), at which point the UI takes over.
    /// </summary>
    private async Task MaintainConnectionAsync()
    {
        if (_maintaining) return;
        _maintaining = true;
        IsBusy = true;
        try
        {
            while (!_cts.IsCancellationRequested && !IsConnected)
            {
                ConnectOutcome outcome;
                try
                {
                    outcome = await _client.ConnectAsync(
                        JoinCode?.Trim() ?? string.Empty,
                        DeviceInfo.Name ?? "Teacher",
                        DeviceInfo.Platform.ToString(),
                        _cts.Token);
                }
                catch (OperationCanceledException) { break; }

                IsConnected = outcome == ConnectOutcome.Connected;
                NeedsJoinCode = outcome == ConnectOutcome.NeedsJoinCode;

                // Connected, or waiting on the user — stop the automatic retry loop.
                if (outcome is ConnectOutcome.Connected
                            or ConnectOutcome.NeedsJoinCode
                            or ConnectOutcome.Incompatible)
                    break;

                // NoServer / Error: the server may simply not be up yet. Wait and try again.
                try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _maintaining = false;
            IsBusy = false;
        }
    }

    private void OnConnectionLost()
    {
        IsConnected = false;
        _ = MaintainConnectionAsync();
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
