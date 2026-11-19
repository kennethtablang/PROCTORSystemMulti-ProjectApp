using PROCTOR.Shared.Contracts;
using Microsoft.Maui.Graphics;

namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Captures the teacher's mouse and keyboard over a view. Positions are handed to the mapper in
/// the view's own coordinates; it returns the normalised (0–1) point on the student's screen, or
/// null when the pointer is outside the picture. Implemented by the Windows head (mouse/keyboard)
/// and the Android head (touch, mouse, keyboards); other heads use <see cref="UnsupportedRemoteInput"/>.
/// </summary>
public interface IRemoteInputCapture
{
    bool IsSupported { get; }

    /// <summary>Start capturing; dispose the result to stop.</summary>
    IDisposable Attach(View surface, Func<Point, Point?> toFrame, Action<RemoteInputEvent> onEvent);

    /// <summary>Touch devices: an on-screen keyboard can type on the student's PC (see <see cref="ShowKeyboard"/>).</summary>
    bool HasOnScreenKeyboard => false;

    /// <summary>While attached: open the on-screen keyboard; what's typed goes to the student's PC.</summary>
    void ShowKeyboard() { }
}

public sealed class UnsupportedRemoteInput : IRemoteInputCapture
{
    public bool IsSupported => false;
    public IDisposable Attach(View surface, Func<Point, Point?> toFrame, Action<RemoteInputEvent> onEvent) =>
        throw new PlatformNotSupportedException("Remote control isn't available on this device.");
}

/// <summary>Platform hooks the app heads fill in at start-up (the shared project targets plain net10.0).</summary>
public static class PlatformServices
{
    public static IRemoteInputCapture RemoteInput { get; set; } = new UnsupportedRemoteInput();

    /// <summary>
    /// Android: hand a downloaded, verified update package to the system installer. Returns a message
    /// for the teacher when something must happen first (e.g. allow installing updates), else null.
    /// </summary>
    public static Func<string, Task<string?>>? InstallPackage { get; set; }

    /// <summary>
    /// Android 12+: true when <see cref="InstallPackage"/> will update the app with no one tapping
    /// anything (PROCTOR installed its current version itself). Null elsewhere.
    /// </summary>
    public static Func<bool>? CanUpdateSilently { get; set; }
}

/// <summary>
/// One teacher controlling one student's PC (FR-CTRL-05): queues input from the focus view,
/// coalesces mouse moves and sends a batch every ~30 ms through the server, which relays it only
/// while this teacher holds control.
/// </summary>
public sealed class RemoteControlSession : IAsyncDisposable
{
    private const int MaxQueued = 512;
    private readonly TeacherClient _client;
    private readonly int _display;
    private readonly object _gate = new();
    private readonly List<RemoteInputEvent> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised (any thread) when sending fails, e.g. control was taken over or the student left.</summary>
    public event Action<string>? Failed;

    public string Student { get; }

    private RemoteControlSession(TeacherClient client, string student, int display)
    {
        _client = client;
        Student = student;
        _display = display;
    }

    /// <summary>Ask the server for control; throws with the server's reason if refused.</summary>
    public static async Task<RemoteControlSession> StartAsync(TeacherClient client, string student, int display)
    {
        await client.StartRemoteControlAsync(student);
        var session = new RemoteControlSession(client, student, display);
        session._loop = Task.Run(() => session.LoopAsync(session._cts.Token));
        Interlocked.Increment(ref _active);
        return session;
    }

    private static int _active;

    /// <summary>Whether this teacher is controlling a PC right now (automatic updates wait).</summary>
    public static bool AnyActive => Volatile.Read(ref _active) > 0;

    public void Post(RemoteInputEvent e)
    {
        lock (_gate)
        {
            if (e.Kind == RemoteInputKind.MouseMove && _queue.Count > 0 && _queue[^1].Kind == RemoteInputKind.MouseMove)
                _queue[^1] = e;   // only the latest position matters
            else if (_queue.Count < MaxQueued)
                _queue.Add(e);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(RemoteControlLimits.FlushInterval, ct); }
            catch (OperationCanceledException) { break; }

            RemoteInputEvent[] batch;
            lock (_gate)
            {
                if (_queue.Count == 0) continue;
                var n = Math.Min(_queue.Count, RemoteControlLimits.MaxEventsPerBatch);
                batch = _queue.GetRange(0, n).ToArray();
                _queue.RemoveRange(0, n);
            }
            try
            {
                await _client.SendRemoteInputAsync(new RemoteInputBatch { StudentConnectionId = Student, DisplayIndex = _display, Events = batch });
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
                break;
            }
        }
    }

    public async Task StopAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        Interlocked.Decrement(ref _active);
        if (_loop is not null) { try { await _loop; } catch { } }
        try { await _client.StopRemoteControlAsync(Student); } catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
