using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PROCTOR.Shared.Contracts;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Backs every teacher tab: owns the <see cref="TeacherClient"/> and the <see cref="StreamManager"/>,
/// keeps the live roster as observable <see cref="StudentTile"/>s, and marshals every server event
/// onto the UI thread. Also drives room choice (FR-DISC-04), the manual server address fallback
/// (NFR-COMPAT-02) and the dashboard's search / sort / density controls (FR-TEACH-06).
/// </summary>
public sealed partial class DashboardViewModel : INotifyPropertyChanged
{
    private const string PrefRoom = "proctor.room";
    private const string PrefServer = "proctor.server";
    private const string PrefThumb = "proctor.thumbwidth";
    private const string PrefFocus = "proctor.focuswidth";
    private const string PrefTileSize = "proctor.tilesize";

    private readonly TeacherClient _client = new();
    private readonly Dictionary<string, StudentTile> _byConnection = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile StudentTile[] _snapshot = [];   // thread-safe roster view for the stream manager
    private bool _maintaining;
    private IDispatcherTimer? _resortTimer;

    /// <summary>Every connected student (Processes and Bandwidth tabs).</summary>
    public ObservableCollection<StudentTile> Students { get; } = new();

    /// <summary>The dashboard grid: <see cref="Students"/> after search and sort (FR-TEACH-06).</summary>
    public ObservableCollection<StudentTile> VisibleStudents { get; } = new();

    /// <summary>Rooms to choose from when several answered discovery (FR-DISC-04).</summary>
    public ObservableCollection<RoomOption> Rooms { get; } = new();

    /// <summary>The shared server link (the focus view uses it to check connectivity).</summary>
    public TeacherClient Client => _client;

    /// <summary>All live screen streams: thumbnails for the grid, one focus stream (FR-STREAM-04).</summary>
    public StreamManager Streams { get; }

    /// <summary>Shows this teacher's screen to the class (FR-CTRL-02).</summary>
    public ScreenBroadcaster Broadcaster { get; }

    public DashboardViewModel() : this(new UnsupportedScreenSource()) { }

    public DashboardViewModel(IScreenSource screen)
    {
        Streams = new StreamManager(_client, () => _snapshot);
        Broadcaster = new ScreenBroadcaster(_client, screen);
        Broadcaster.Changed += () =>
        {
            Streams.SetPinned(Broadcaster.IsActive ? Broadcaster.ShowingStudent?.ConnectionId : null);
            OnUi(() => { Raise(nameof(IsBroadcasting)); Raise(nameof(BroadcastText)); });
        };

        _client.Status += s => OnUi(() => Status = s);
        _client.RosterUpdated += r => OnUi(() => ReplaceRoster(r));
        _client.StudentJoined += s => OnUi(() => AddOrUpdate(s));
        _client.StudentLeft += id => OnUi(() => Remove(id));
        _client.StudentStateChanged += (id, st) => OnUi(() => SetState(id, st));
        _client.TelemetryUpdated += (id, t) => OnUi(() => ApplyTelemetry(id, t));
        // The connection dropped for good (server restart / network loss): drop our roster and
        // start re-discovering so we latch back on automatically (FR-DISC-05).
        _client.ConnectionLost += () => OnUi(OnConnectionLost);

        // Classroom control (P5).
        _client.StudentLockChanged += (id, locked) => OnUi(() => { if (_byConnection.TryGetValue(id, out var t)) t.IsLocked = locked; });
        _client.StudentHandChanged += (id, raised) => OnUi(() => SetHand(id, raised));
        _client.PolicyUpdated += p => OnUi(() => Policy = p);
        _client.PollResultsUpdated += r => OnUi(() => PollResults = r);
        _client.TimerUpdated += t => OnUi(() => OnTimerUpdated(t));
        _client.ChatReceived += m => OnUi(() => OnChat(m));
        // File chunks are handled in order on the hub's thread (writes are small and sequential);
        // only the UI-facing bits are marshalled.
        _client.FileOffered += o => OnUi(() => OnFileOffered(o));
        _client.FileChunkReceived += c => OnUi(() => OnFileChunk(c));
        _client.FileFinished += f => OnUi(() => OnFileFinished(f));
        _client.FileCancelled += id => OnUi(() => OnFileCancelled(id));
        _client.FileResultReceived += r => OnUi(() => OnFileResult(r));
        // A restarted server has forgotten the room's limits: re-apply the ones this teacher set.
        _client.Registered += () => { if (!_lastSetPolicy.IsEmpty) _ = SetPolicyAsync(_lastSetPolicy); };

        ConnectCommand = new Command(async () => await ConnectWithCodeAsync(), () => !IsBusy && !IsConnected);
        ConnectManualCommand = new Command(async () => await ConnectManualAsync(), () => !IsBusy && !IsConnected);
        SelectRoomCommand = new Command<RoomOption>(async r => await SelectRoomAsync(r));
        TrustServerCommand = new Command(async () => await TrustServerAsync());
        RejectServerCommand = new Command(RejectServer);
        SwitchRoomCommand = new Command(async () => await SwitchRoomAsync(), () => IsConnected);
        MoreColumnsCommand = new Command(() => Columns++);
        FewerColumnsCommand = new Command(() => Columns--);
        InitConnectGuide();

        _manualAddress = LoadPref(PrefServer);

        // Stream resolution settings (remembered per teacher device).
        _thumbIndex = IndexOf(StreamResolution.ThumbnailChoices, LoadPref(PrefThumb), 1);
        Streams.ThumbnailWidth = StreamResolution.ThumbnailChoices[_thumbIndex].Width;
        var defaultFocus = IndexOf(StreamResolution.FocusChoices, StreamResolution.DefaultFocusWidth.ToString(), 1);
        _focusIndex = IndexOf(StreamResolution.FocusChoices, LoadPref(PrefFocus), defaultFocus);
        Streams.FocusWidth = StreamResolution.FocusChoices[_focusIndex].Width;
        _columns = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1 : DeviceInfo.Idiom == DeviceIdiom.Tablet ? 2 : 3;
        _tileSize = double.TryParse(LoadPref(PrefTileSize), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var size) ? Math.Clamp(size, MinTileSize, MaxTileSize) : 320;
    }

    // ---- connection state -------------------------------------------------

    private string _joinCode = string.Empty;
    public string JoinCode { get => _joinCode; set { _joinCode = value; Raise(); } }

    private string _status = "Looking for the classroom server…";
    public string Status { get => _status; private set { _status = value; Raise(); ShowStatusBriefly(); } }

    private bool _statusShown = true;
    private IDispatcherTimer? _statusTimer;
    /// <summary>The status pill at the top right: shown for a few seconds after each message, and while searching.</summary>
    public bool StatusVisible => _statusShown || _isSearching;

    private void ShowStatusBriefly()
    {
        _statusShown = true;
        Raise(nameof(StatusVisible));
        try
        {
            if (_statusTimer is null)
            {
                if (Application.Current?.Dispatcher is not { } dispatcher) return;
                _statusTimer = dispatcher.CreateTimer();
                _statusTimer.Interval = TimeSpan.FromSeconds(5);
                _statusTimer.IsRepeating = false;
                _statusTimer.Tick += (_, _) => { _statusShown = false; Raise(nameof(StatusVisible)); };
            }
            _statusTimer.Stop();
            _statusTimer.Start();
        }
        catch
        {
            // No UI dispatcher yet (start-up): the message simply stays until the next one.
        }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            _isConnected = value;
            Raise(); Raise(nameof(NotConnected)); Raise(nameof(RoomTitle));
            if (value) { NeedsRoomChoice = false; ShowManualEntry = false; }
            RefreshCommands();
            RaiseConnectGuide();
        }
    }
    public bool NotConnected => !_isConnected;

    public string RoomTitle => _isConnected && _client.CurrentServer is { } s ? s.RoomName : "";

    // A teacher-initiated attempt (code / manual address / room pick) is in flight.
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Raise(); RefreshCommands(); } }

    // The background discover/reconnect loop is running (doesn't block the manual controls).
    private bool _isSearching;
    public bool IsSearching { get => _isSearching; private set { _isSearching = value; Raise(); Raise(nameof(StatusVisible)); } }

    /// <summary>
    /// False by default so the app "just opens and connects". It flips true only when a
    /// server is found but rejects the no-code attempt (i.e. the room is running with a
    /// teacher code), at which point the UI reveals the code box for manual entry.
    /// </summary>
    private bool _needsJoinCode;
    public bool NeedsJoinCode { get => _needsJoinCode; private set { _needsJoinCode = value; Raise(); } }

    private bool _needsRoomChoice;
    public bool NeedsRoomChoice { get => _needsRoomChoice; private set { _needsRoomChoice = value; Raise(); } }

    /// <summary>Shown once discovery has come up empty: lets the teacher type the server address.</summary>
    private bool _showManualEntry;
    public bool ShowManualEntry { get => _showManualEntry; private set { _showManualEntry = value; Raise(); } }

    private string _manualAddress;
    public string ManualAddress { get => _manualAddress; set { _manualAddress = value; Raise(); } }

    public int StudentCount => Students.Count;
    public bool HasStudents => Students.Count > 0;

    public ICommand ConnectCommand { get; }
    public ICommand ConnectManualCommand { get; }
    public ICommand SelectRoomCommand { get; }
    public ICommand TrustServerCommand { get; }
    public ICommand RejectServerCommand { get; }

    // ---- server identity (NFR-SEC-06) ----
    private bool _needsServerTrust;
    public bool NeedsServerTrust { get => _needsServerTrust; private set { _needsServerTrust = value; Raise(); } }
    public string TrustTitle { get; private set; } = "";
    public string TrustDetail { get; private set; } = "";
    public string TrustFingerprint { get; private set; } = "";
    public bool TrustIsChange { get; private set; }
    public Color TrustColor => TrustIsChange ? Color.FromArgb("#C62828") : Color.FromArgb("#5B2C9E");
    public string TrustButtonText => TrustIsChange ? "Trust the NEW identity" : "Trust and connect";

    private void ShowPendingTrust()
    {
        if (_client.PendingTrust is not { } p)
        {
            NeedsServerTrust = false;
            return;
        }
        TrustIsChange = p.Previous is not null;
        TrustFingerprint = global::PROCTOR.Shared.ServerTrust.Format(p.Fingerprint);
        TrustTitle = TrustIsChange
            ? $"Warning: the identity of \u201C{p.Server.RoomName}\u201D has CHANGED"
            : $"New classroom server: \u201C{p.Server.RoomName}\u201D ({p.Server.Host})";
        TrustDetail = TrustIsChange
            ? "This server presents a different key from the one you trusted before. That happens if the server was reinstalled — "
              + "but it is also what an impostor would look like. Only continue if IT confirms the new fingerprint below "
              + "matches the one printed on the server's screen."
            : "Check that this fingerprint matches the one printed on the classroom server's screen, then trust it. "
              + "You're only asked once per server.";
        Raise(nameof(TrustTitle)); Raise(nameof(TrustDetail)); Raise(nameof(TrustFingerprint));
        Raise(nameof(TrustIsChange)); Raise(nameof(TrustColor)); Raise(nameof(TrustButtonText));
        NeedsServerTrust = true;
    }

    private async Task TrustServerAsync()
    {
        if (_client.TrustPending() is not { } server) return;
        NeedsServerTrust = false;
        await RunAttemptAsync(server);
    }

    private void RejectServer()
    {
        NeedsServerTrust = false;
        Status = "Not connected: the server's identity wasn't confirmed.";
    }
    public ICommand SwitchRoomCommand { get; }
    public ICommand MoreColumnsCommand { get; }
    public ICommand FewerColumnsCommand { get; }

    // ---- classroom control (P5) --------------------------------------------

    private ClassPolicy _policy = new();
    private ClassPolicy _lastSetPolicy = new();

    /// <summary>The room's current web/app/internet/sound limits (as the server holds them).</summary>
    public ClassPolicy Policy
    {
        get => _policy;
        private set { _policy = value; Raise(); Raise(nameof(PolicySummary)); Raise(nameof(HasPolicy)); }
    }

    public bool HasPolicy => !_policy.IsEmpty;

    public string PolicySummary
    {
        get
        {
            if (_policy.IsEmpty) return "No limits";
            var parts = new List<string>();
            if (_policy.WebMode == WebLimitMode.BlockListed && _policy.WebHosts.Count > 0) parts.Add($"{_policy.WebHosts.Count} site(s) blocked");
            if (_policy.WebMode == WebLimitMode.AllowOnlyListed) parts.Add($"only {_policy.WebHosts.Count} site(s) allowed");
            if (_policy.BlockedApps.Count > 0) parts.Add($"{_policy.BlockedApps.Count} app(s) blocked");
            if (_policy.BlockInternet) parts.Add("internet off");
            if (_policy.MuteSound) parts.Add("sound muted");
            return "Limits: " + string.Join(" · ", parts);
        }
    }

    // ---- screen broadcast ----------------------------------------------------

    public bool IsBroadcasting => Broadcaster.IsActive;

    public string BroadcastText => Broadcaster.IsActive
        ? $"Showing {(Broadcaster.ShowingStudent is { } shown ? shown.MachineName + "'s" : "your")} screen ({(Broadcaster.Mode == BroadcastMode.FullScreen ? "full screen" : "in a window")}) · {Broadcaster.Viewers} watching"
        : "";

    // ---- polls -------------------------------------------------------------

    private PollResults? _pollResults;
    /// <summary>Live results of the current (or last) poll; null before the first poll.</summary>
    public PollResults? PollResults
    {
        get => _pollResults;
        private set { _pollResults = value; Raise(); Raise(nameof(PollBadge)); }
    }

    /// <summary>Short live status for the dashboard bar, e.g. "Poll: 12/20 answered".</summary>
    public string PollBadge => _pollResults is { Closed: false } r ? $"Poll open · {r.Answered}/{r.Asked} answered" : "";

    public async Task<string?> StartPollAsync(Poll poll)
    {
        try { await _client.StartPollAsync(poll); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task<string?> ClosePollAsync()
    {
        try { await _client.ClosePollAsync(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public int RaisedHands => Students.Count(s => s.HandRaised);
    public bool HasRaisedHands => RaisedHands > 0;
    public string RaisedHandsText => RaisedHands == 1 ? "1 student needs help" : $"{RaisedHands} students need help";

    private void SetHand(string id, bool raised)
    {
        if (!_byConnection.TryGetValue(id, out var tile)) return;
        tile.HandRaised = raised;
        RaiseHands();
        RefreshVisible();   // raised hands float to the top of the grid
    }

    private void RaiseHands()
    {
        Raise(nameof(RaisedHands));
        Raise(nameof(HasRaisedHands));
        Raise(nameof(RaisedHandsText));
    }

    /// <summary>Send a command to the given students (null/empty = whole class). Returns an error to show, or null.</summary>
    public async Task<string?> SendCommandAsync(CommandKind kind, IReadOnlyList<string>? targets = null, string? text = null, string? url = null)
    {
        try
        {
            var n = await _client.SendCommandAsync(new ClassroomCommand
            {
                Kind = kind,
                Targets = targets ?? [],
                Text = text,
                Url = url,
            });
            Status = n == 0 ? "No students to send to." : $"{Describe(kind)} sent to {n} student{(n == 1 ? "" : "s")}.";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Give students their own website limits (null: the class's). Returns an error to show, or null.</summary>
    public async Task<string?> SetStudentWebLimitsAsync(IReadOnlyList<string> students, WebLimits? limits)
    {
        try
        {
            await _client.SetStudentWebLimitsAsync(students, limits);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Set the room's limits. Returns an error to show, or null.</summary>
    public async Task<string?> SetPolicyAsync(ClassPolicy policy)
    {
        try
        {
            var applied = await _client.SetPolicyAsync(policy);
            _lastSetPolicy = applied;
            OnUi(() => Policy = applied);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public Task LowerHandAsync(StudentTile tile) => _client.LowerHandAsync(tile.ConnectionId);

    private static string Describe(CommandKind kind) => kind switch
    {
        CommandKind.Message => "Message",
        CommandKind.Lock => "Lock",
        CommandKind.Unlock => "Unlock",
        CommandKind.OpenUrl => "Web page",
        CommandKind.LogOff => "Log off",
        CommandKind.Restart => "Restart",
        CommandKind.ShutDown => "Shut down",
        _ => kind.ToString(),
    };

    // ---- stream resolution -------------------------------------------------

    public IReadOnlyList<string> ThumbnailChoices { get; } = StreamResolution.ThumbnailChoices.Select(c => c.Label).ToArray();
    public IReadOnlyList<string> FocusChoices { get; } = StreamResolution.FocusChoices.Select(c => c.Label).ToArray();

    private int _thumbIndex;
    /// <summary>Grid thumbnail size (small / normal / large).</summary>
    public int ThumbnailSizeIndex
    {
        get => _thumbIndex;
        set
        {
            if (value < 0 || value >= StreamResolution.ThumbnailChoices.Count || value == _thumbIndex) return;
            _thumbIndex = value; Raise();
            var w = StreamResolution.ThumbnailChoices[value].Width;
            Streams.ThumbnailWidth = w;
            SavePref(PrefThumb, w.ToString());
        }
    }

    private int _focusIndex;
    /// <summary>Focus-view stream resolution.</summary>
    public int FocusResolutionIndex
    {
        get => _focusIndex;
        set
        {
            if (value < 0 || value >= StreamResolution.FocusChoices.Count || value == _focusIndex) return;
            _focusIndex = value; Raise();
            var w = StreamResolution.FocusChoices[value].Width;
            Streams.FocusWidth = w;
            SavePref(PrefFocus, w.ToString());
        }
    }

    private static int IndexOf(IReadOnlyList<(string Label, int Width)> choices, string saved, int fallback)
    {
        if (!int.TryParse(saved, out var w)) return fallback;
        for (var i = 0; i < choices.Count; i++) if (choices[i].Width == w) return i;
        return fallback;
    }

    // ---- search / sort / density (FR-TEACH-06) ----------------------------

    public IReadOnlyList<string> SortOptions { get; } = ["Name", "CPU", "Memory", "Bandwidth"];

    private int _sortIndex;
    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (_sortIndex == value || value < 0) return;
            _sortIndex = value; Raise(); RefreshVisible();
            EnsureResortTimer();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set { _searchText = value ?? ""; Raise(); RefreshVisible(); } }

    private int _columns;
    /// <summary>Tiles per row in the dashboard grid (1–8).</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            var v = Math.Clamp(value, 1, 12);
            if (v == _columns) return;
            _columns = v; Raise(); Raise(nameof(ColumnsText));
        }
    }
    public string ColumnsText => $"{_columns} per row";

    public const double MinTileSize = 180;
    public const double MaxTileSize = 960;
    private double _tileSize;
    /// <summary>How wide a student screen should be on the dashboard (the Size slider); columns follow.</summary>
    public double TileSize
    {
        get => _tileSize;
        set
        {
            var v = Math.Clamp(Math.Round(value), MinTileSize, MaxTileSize);
            if (Math.Abs(v - _tileSize) < 1) return;
            _tileSize = v; Raise();
            SavePref(PrefTileSize, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public string EmptyText => Students.Count > 0 ? "No students match your search." : "No students connected yet.";

    public string VisibleCountText => VisibleStudents.Count == Students.Count
        ? $"{Students.Count} online"
        : $"{VisibleStudents.Count} of {Students.Count} shown";

    // ---- connecting -------------------------------------------------------

    private bool _autoTried;

    /// <summary>
    /// Called once when the first tab appears: try to connect with no code. On an open server
    /// this lands straight on the live grid; only if the server asks for a code (or several
    /// rooms answer) does the UI ask the teacher anything.
    /// </summary>
    public Task AutoConnectAsync()
    {
        if (_autoTried) return Task.CompletedTask;
        _autoTried = true;
        return MaintainConnectionAsync();
    }

    /// <summary>Connect button next to the join-code box: retry the same server with the typed code.</summary>
    private async Task ConnectWithCodeAsync()
    {
        if (IsBusy || IsConnected) return;
        await RunAttemptAsync(_client.CurrentServer);
    }

    private async Task ConnectManualAsync()
    {
        if (IsBusy || IsConnected) return;
        var address = ManualAddress?.Trim() ?? "";
        if (!global::PROCTOR.Shared.Discovery.ManualServer.TryParse(address, out _, out _))
        {
            Status = $"Enter the server address as host or host:port, e.g. {StudentServerAddress}.";
            return;
        }

        global::PROCTOR.Shared.Contracts.DiscoveryResponse? server;
        IsBusy = true;
        try
        {
            Status = $"Contacting {address}…";
            server = await global::PROCTOR.Shared.Discovery.ManualServer.ResolveAsync(address, TimeSpan.FromSeconds(5), _cts.Token);
            if (server is null)
            {
                Status = $"No PROCTOR server answered at {address}.";
                return;
            }
            SavePref(PrefServer, address);

            // The typed join code belongs to the server that asked for it; never offer it to a
            // different machine the teacher dials by hand.
            if (_client.CurrentServer is not { } asked
                || !string.Equals(asked.Host, server.Host, StringComparison.OrdinalIgnoreCase)
                || asked.Port != server.Port)
                ClearJoinCode();
        }
        finally
        {
            IsBusy = false;
        }

        // Dial exactly the server that answered (the address is also remembered for reconnects).
        await RunAttemptAsync(server);
    }

    private async Task SelectRoomAsync(RoomOption? room)
    {
        if (room is null || IsBusy || IsConnected) return;
        NeedsRoomChoice = false;
        ClearJoinCode();   // a code typed for another room must not be sent to this one
        SavePref(PrefRoom, room.Server.RoomName);
        await RunAttemptAsync(room.Server);
    }

    private async Task SwitchRoomAsync()
    {
        // Forget the remembered room and current server, then discover afresh (shows the chooser
        // if several rooms answer).
        SavePref(PrefRoom, "");
        ClearJoinCode();   // the old room's code is never offered to whichever room answers next
        ClearRoster();
        await _client.ForgetServerAsync();
        IsConnected = false;
        await MaintainConnectionAsync();
    }

    /// <summary>One attempt at a specific server (picked room / code retry), then hand back to the loop if it fails.</summary>
    private async Task RunAttemptAsync(global::PROCTOR.Shared.Contracts.DiscoveryResponse? target)
    {
        IsBusy = true;
        ConnectOutcome outcome;
        try
        {
            outcome = await _client.ConnectAsync(JoinCode?.Trim() ?? "", DeviceName, Platform, _cts.Token, target: target);
            ApplyOutcome(outcome);
        }
        finally
        {
            IsBusy = false;
        }

        if (outcome is ConnectOutcome.NoServer or ConnectOutcome.Error)
            _ = MaintainConnectionAsync();
    }

    /// <summary>
    /// Keeps trying to reach a classroom server until connected. Handles "the server isn't up yet"
    /// at launch and full re-discovery after a drop (FR-DISC-05): it retries every few seconds on
    /// NoServer/Error, and stops once connected or when the teacher has to act (enter a join code,
    /// pick a room, or update an incompatible app).
    /// </summary>
    private async Task MaintainConnectionAsync()
    {
        if (_maintaining) return;
        _maintaining = true;
        IsSearching = true;
        try
        {
            while (!_cts.IsCancellationRequested && !IsConnected)
            {
                ConnectOutcome outcome;
                try
                {
                    outcome = await _client.ConnectAsync(
                        JoinCode?.Trim() ?? string.Empty,
                        DeviceName,
                        Platform,
                        _cts.Token,
                        preferredRoom: LoadPref(PrefRoom),
                        manualAddress: string.IsNullOrWhiteSpace(ManualAddress) ? null : ManualAddress.Trim());
                }
                catch (OperationCanceledException) { break; }

                ApplyOutcome(outcome);

                if (outcome is ConnectOutcome.Connected
                            or ConnectOutcome.ConfirmServer
                            or ConnectOutcome.NeedsJoinCode
                            or ConnectOutcome.ChooseRoom
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
            IsSearching = false;
        }
    }

    private void ApplyOutcome(ConnectOutcome outcome)
    {
        IsConnected = outcome == ConnectOutcome.Connected;
        NeedsJoinCode = outcome == ConnectOutcome.NeedsJoinCode;
        if (outcome == ConnectOutcome.ConfirmServer) ShowPendingTrust();
        else NeedsServerTrust = false;

        if (outcome == ConnectOutcome.ChooseRoom)
        {
            Rooms.Clear();
            foreach (var r in _client.DiscoveredRooms) Rooms.Add(new RoomOption(r));
            NeedsRoomChoice = true;
        }
        else
        {
            NeedsRoomChoice = false;
        }

        // No server, or the one found wants a code: let the teacher type another address (e.g. the
        // lab teacher PC from a laptop whose own network has a different PROCTOR server).
        // Several rooms: the teacher may want one that didn't answer discovery, so allow typing too.
        if (outcome is ConnectOutcome.NoServer or ConnectOutcome.NeedsJoinCode || NeedsRoomChoice) ShowManualEntry = true;
        if (outcome == ConnectOutcome.Connected && _client.CurrentServer is { } s) SavePref(PrefRoom, s.RoomName);
    }

    private void ClearJoinCode()
    {
        JoinCode = string.Empty;
        NeedsJoinCode = false;
    }

    private void OnConnectionLost()
    {
        IsConnected = false;
        ResetFiles();
        _ = MaintainConnectionAsync();
    }

    private static string DeviceName => DeviceInfo.Name ?? "Teacher";
    private static string Platform => DeviceInfo.Platform.ToString();

    // ---- roster -----------------------------------------------------------

    private void ReplaceRoster(RosterSnapshot roster)
    {
        Students.Clear();
        _byConnection.Clear();
        foreach (var s in roster.Students) AddOrUpdate(s, refresh: false);
        RosterChanged();
    }

    private void ClearRoster()
    {
        Students.Clear();
        _byConnection.Clear();
        RosterChanged();
    }

    private void AddOrUpdate(StudentSnapshot snap) => AddOrUpdate(snap, refresh: true);

    private void AddOrUpdate(StudentSnapshot snap, bool refresh)
    {
        TrackJoin(snap);
        if (_byConnection.TryGetValue(snap.ConnectionId, out var existing))
        {
            existing.State = snap.State;
            existing.OwnWebLimits = snap.OwnWebLimits;
            return;
        }
        var tile = new StudentTile(snap);
        tile.PropertyChanged += OnTileChanged;   // selection count for the toolbar
        _byConnection[snap.ConnectionId] = tile;
        Students.Add(tile);
        if (refresh) RosterChanged();
    }

    private void Remove(string connectionId)
    {
        TrackLeave(connectionId);
        if (Broadcaster.IsActive && Broadcaster.ShowingStudent?.ConnectionId == connectionId)
            _ = Broadcaster.StopAsync();   // the shown student left
        if (_byConnection.Remove(connectionId, out var tile))
        {
            Students.Remove(tile);
            RosterChanged();
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

    private void RosterChanged()
    {
        _snapshot = Students.ToArray();
        RaiseHands();
        Raise(nameof(StudentCount));
        Raise(nameof(HasStudents));
        Raise(nameof(ShowConnectGuide));
        RefreshVisible();
        SyncLayout();
        RaiseSelection();
        Streams.RosterChanged();
    }

    /// <summary>
    /// Rebuild <see cref="VisibleStudents"/> from search + sort with minimal moves, so tiles (and
    /// their live video surfaces) are reordered rather than recreated.
    /// </summary>
    private void RefreshVisible()
    {
        IEnumerable<StudentTile> q = Students;

        var term = _searchText.Trim();
        if (term.Length > 0)
            q = q.Where(t => t.Header.Contains(term, StringComparison.OrdinalIgnoreCase)
                             || t.ForegroundText.Contains(term, StringComparison.OrdinalIgnoreCase)
                             || t.ActiveHostText.Contains(term, StringComparison.OrdinalIgnoreCase));

        // Students asking for help always come first, then the chosen sort.
        var byHand = q.OrderByDescending(t => t.HandRaised);
        q = _sortIndex switch
        {
            1 => byHand.ThenByDescending(t => t.CpuPercent).ThenBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase),
            2 => byHand.ThenByDescending(t => t.MemoryPercent).ThenBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase),
            3 => byHand.ThenByDescending(t => t.TotalRate).ThenBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase),
            _ => byHand.ThenBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.UserName),
        };

        var desired = q.ToList();
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < VisibleStudents.Count && ReferenceEquals(VisibleStudents[i], item)) continue;
            var at = VisibleStudents.IndexOf(item);
            if (at >= 0) VisibleStudents.Move(at, i);
            else VisibleStudents.Insert(i, item);
        }
        while (VisibleStudents.Count > desired.Count) VisibleStudents.RemoveAt(VisibleStudents.Count - 1);

        Raise(nameof(VisibleCountText));
        Raise(nameof(EmptyText));
    }

    /// <summary>Live-metric sorts re-order every few seconds rather than on every sample, so tiles don't jitter.</summary>
    private void EnsureResortTimer()
    {
        if (_sortIndex == 0)
        {
            _resortTimer?.Stop();
            return;
        }
        if (_resortTimer is null)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            _resortTimer = dispatcher.CreateTimer();
            _resortTimer.Interval = TimeSpan.FromSeconds(3);
            _resortTimer.Tick += (_, _) => RefreshVisible();
        }
        _resortTimer.Start();
    }

    // ---- helpers ----------------------------------------------------------

    private void RefreshCommands()
    {
        (ConnectCommand as Command)?.ChangeCanExecute();
        (ConnectManualCommand as Command)?.ChangeCanExecute();
        (SwitchRoomCommand as Command)?.ChangeCanExecute();
    }

    private static string LoadPref(string key)
    {
        try { return Preferences.Default.Get(key, string.Empty); }
        catch { return string.Empty; }
    }

    private static void SavePref(string key, string value)
    {
        try { Preferences.Default.Set(key, value); }
        catch { /* preferences unavailable (e.g. unpackaged without storage) — non-essential */ }
    }

    private static void OnUi(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A room offered in the chooser (FR-DISC-04).</summary>
public sealed record RoomOption(global::PROCTOR.Shared.Contracts.DiscoveryResponse Server)
{
    public string Title => Server.RoomName;
    /// <summary>Address, marked "this PC" when the room runs here (rooms often share a name).</summary>
    public string Detail => IsThisPc ? $"{Server.Host}:{Server.Port} · this PC" : $"{Server.Host}:{Server.Port}";

    private bool IsThisPc
    {
        get
        {
            if (Server.Host is "127.0.0.1" or "localhost") return true;
            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Any(a => a.Address.ToString() == Server.Host);
            }
            catch
            {
                return false;
            }
        }
    }
}
