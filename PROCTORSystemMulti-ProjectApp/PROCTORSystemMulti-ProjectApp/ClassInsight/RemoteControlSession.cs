using ClassInsight.Shared.Contracts;
using Microsoft.Maui.Graphics;

namespace PROCTORSystemMulti_ProjectApp.ClassInsight;

/// <summary>
/// Captures the teacher's mouse and keyboard over a view. Positions are handed to the mapper in
/// the view's own coordinates; it returns the normalised (0–1) point on the student's screen, or
/// null when the pointer is outside the picture. Implemented by the Windows head; other heads use
/// <see cref="UnsupportedRemoteInput"/>.
/// </summary>
public interface IRemoteInputCapture
{
    bool IsSupported { get; }

    /// <summary>Start capturing; dispose the result to stop.</summary>
    IDisposable Attach(View surface, Func<Point, Point?> toFrame, Action<RemoteInputEvent> onEvent);
}

public sealed class UnsupportedRemoteInput : IRemoteInputCapture
{
    public bool IsSupported => false;
    public IDisposable Attach(View surface, Func<Point, Point?> toFrame, Action<RemoteInputEvent> onEvent) =>
        throw new PlatformNotSupportedException("Remote control is available on the Windows teacher app.");
}

/// <summary>Platform hooks the app heads fill in at start-up (the shared project targets plain net10.0).</summary>
public static class PlatformServices
{
    public static IRemoteInputCapture RemoteInput { get; set; } = new UnsupportedRemoteInput();
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
        return session;
    }

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
        if (_loop is not null) { try { await _loop; } catch { } }
        try { await _client.StopRemoteControlAsync(Student); } catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
