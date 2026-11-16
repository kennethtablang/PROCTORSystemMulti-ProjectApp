using System.Collections.Concurrent;
using ClassInsight.Shared.Contracts;
using Microsoft.Maui.ApplicationModel;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// Owns every live screen stream the teacher is receiving — at most one per student — and keeps
/// them matched to what is on screen (FR-STREAM-04):
///
///  * while the dashboard grid is visible, every student streams a <b>thumbnail</b>
///    (downscaled, low fps) into its tile;
///  * the student open in the focus view streams at <b>focus</b> quality (full size, higher fps),
///    on whichever of their displays was picked (FR-AGENT-04);
///  * everything else is stopped, so agents only encode what someone is looking at
///    (FR-STREAM-06, R-3).
///
/// Changes are coalesced and applied by <see cref="ReconcileAsync"/>; switching a student between
/// thumbnail and focus is a re-request the agent answers with a new offer. A watchdog retries
/// streams that failed or never got an offer. It also routes the student's relayed offer/ICE from
/// the single hub connection to the right receiver.
/// </summary>
public sealed class StreamManager : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(3);

    // A stream that stops being wanted is kept this long before it is torn down. Page
    // transitions (grid → focus, a picker popup) briefly report "nothing visible"; with the
    // grace period those become an in-place quality/display switch instead of stop + restart.
    private static readonly TimeSpan StopGrace = TimeSpan.FromMilliseconds(1500);
    private readonly Dictionary<string, DateTimeOffset> _unwantedSince = new();

    private readonly TeacherClient _client;
    private readonly Func<IReadOnlyCollection<StudentTile>> _students;
    // Mutated only under _lock; concurrent so the signalling threads can look up without it.
    private readonly ConcurrentDictionary<string, ScreenStreamReceiver> _receivers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private bool _gridVisible;
    private string? _focusId;
    private string? _pinnedId;
    private int _focusDisplay;
    private int _thumbnailWidth = StreamResolution.DefaultThumbnailWidth;
    private int _focusWidth = StreamResolution.DefaultFocusWidth;

    /// <summary>Requested width of every grid thumbnail (teacher setting); changing it re-requests the streams.</summary>
    public int ThumbnailWidth
    {
        get => _thumbnailWidth;
        set { if (_thumbnailWidth != value) { _thumbnailWidth = value; Schedule(); } }
    }

    /// <summary>Requested width of the focus view stream (teacher setting).</summary>
    public int FocusWidth
    {
        get => _focusWidth;
        set { if (_focusWidth != value) { _focusWidth = value; Schedule(); } }
    }
    private int _scheduled;

    public StreamManager(TeacherClient client, Func<IReadOnlyCollection<StudentTile>> students)
    {
        _client = client;
        _students = students;

        _client.OfferReceived += offer => _ = RouteOfferAsync(offer);
        _client.IceCandidateReceived += OnIce;
        // After every (re)registration the server has no pairs for us any more: start over.
        _client.Registered += () => _ = ResetAsync();
        _client.ConnectionLost += () => _ = ResetAsync();

        _ = WatchdogAsync(_cts.Token);
    }

    /// <summary>The dashboard grid became visible / hidden.</summary>
    public void SetGridVisible(bool visible)
    {
        _gridVisible = visible;
        Schedule();
    }

    /// <summary>Open (or close, with null) the focus view on a student and display.</summary>
    public void SetFocus(string? studentConnectionId, int displayIndex = 0)
    {
        _focusId = studentConnectionId;
        _focusDisplay = Math.Max(0, displayIndex);
        Schedule();
    }

    /// <summary>
    /// Keep a full-quality stream from this student even when the focus view isn't open (their screen
    /// is being shown to the class); null releases it.
    /// </summary>
    public void SetPinned(string? studentConnectionId)
    {
        if (_pinnedId == studentConnectionId) return;
        _pinnedId = studentConnectionId;
        Schedule();
    }

    /// <summary>The roster changed (join/leave/replace).</summary>
    public void RosterChanged() => Schedule();

    private (StreamQuality Quality, int Display, int Width)? DesiredFor(string id) =>
        id == _focusId ? (StreamQuality.Focus, _focusDisplay, _focusWidth)
        : id == _pinnedId ? (StreamQuality.Focus, 0, Math.Max(_focusWidth, BroadcastLimits.Width))
        : _gridVisible ? (StreamQuality.Thumbnail, 0, _thumbnailWidth)
        : null;

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(Debounce, _cts.Token); }
            catch (OperationCanceledException) { return; }
            Interlocked.Exchange(ref _scheduled, 0);
            await ReconcileAsync();
        });
    }

    private void ScheduleAfter(TimeSpan delay) => _ = Task.Run(async () =>
    {
        try { await Task.Delay(delay + TimeSpan.FromMilliseconds(50), _cts.Token); }
        catch (OperationCanceledException) { return; }
        await ReconcileAsync();
    });

    /// <summary>Start, switch or stop streams so they match the current view state.</summary>
    public async Task ReconcileAsync()
    {
        if (_cts.IsCancellationRequested) return;
        await _lock.WaitAsync();
        try
        {
            if (!_client.IsConnected) return;

            var tiles = _students().ToDictionary(t => t.ConnectionId);
            var now = DateTimeOffset.UtcNow;

            // Stop what is no longer wanted (left the roster, or not visible any more) — after a
            // short grace for streams of students still in the room.
            foreach (var (id, receiver) in _receivers.ToArray())
            {
                if (tiles.ContainsKey(id) && DesiredFor(id) is not null)
                {
                    _unwantedSince.Remove(id);
                    continue;
                }
                if (tiles.ContainsKey(id))
                {
                    if (!_unwantedSince.TryGetValue(id, out var since))
                    {
                        _unwantedSince[id] = now;
                        ScheduleAfter(StopGrace);
                        continue;
                    }
                    if (now - since < StopGrace) continue;
                }
                _unwantedSince.Remove(id);
                _receivers.TryRemove(id, out _);
                await receiver.DisposeAsync();
                if (tiles.TryGetValue(id, out var gone))
                {
                    gone.Frame.Clear();
                    OnUi(() => gone.StreamStatus = "");
                }
            }

            // Start or switch what is wanted.
            foreach (var (id, tile) in tiles)
            {
                if (DesiredFor(id) is not { } want) continue;

                if (_receivers.TryGetValue(id, out var existing))
                {
                    var stale = existing.Failed || (existing.AwaitingOffer && now - existing.RequestedAt > OfferTimeout);
                    if (existing.Quality == want.Quality && existing.DisplayIndex == want.Display
                        && existing.Width == want.Width && !stale) continue;

                    if (stale)
                    {
                        // Start clean rather than re-requesting on a dead peer.
                        _receivers.TryRemove(id, out _);
                        await existing.DisposeAsync();
                    }
                    else
                    {
                        await existing.RequestAsync(want.Quality, want.Display, want.Width);
                        continue;
                    }
                }

                var receiver = new ScreenStreamReceiver(_client, tile, status => OnUi(() => tile.StreamStatus = status));
                _receivers[id] = receiver;
                await receiver.RequestAsync(want.Quality, want.Display, want.Width);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RouteOfferAsync(SdpMessage offer)
    {
        if (_receivers.TryGetValue(offer.FromConnectionId, out var receiver))
        {
            try { await receiver.HandleOfferAsync(offer); }
            catch { /* surfaces as Failed; the watchdog retries */ }
        }
    }

    private void OnIce(IceCandidateMessage candidate)
    {
        if (_receivers.TryGetValue(candidate.FromConnectionId, out var receiver))
            receiver.HandleIceCandidate(candidate);
    }

    /// <summary>Drop every receiver (connection reset) and rebuild from the current view state.</summary>
    private async Task ResetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var id in _receivers.Keys.ToArray())
                if (_receivers.TryRemove(id, out var receiver))
                    await receiver.DisposeAsync();
            _unwantedSince.Clear();
        }
        finally
        {
            _lock.Release();
        }
        Schedule();
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Watchdog);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await ReconcileAsync();
        }
        catch (OperationCanceledException) { }
    }

    private static void OnUi(Action action)
    {
        if (MainThread.IsMainThread) action();
        else MainThread.BeginInvokeOnMainThread(action);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await ResetAsync();
    }
}
