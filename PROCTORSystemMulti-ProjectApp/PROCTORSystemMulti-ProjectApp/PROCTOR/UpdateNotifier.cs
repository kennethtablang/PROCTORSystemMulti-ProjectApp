namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Checks GitHub for a newer PROCTOR shortly after the app opens and then every few hours while it
/// is in use.
///
/// Normally it shows the update pop-up on top of whatever page the teacher is on (waiting while the
/// app is PIN-locked, never stacking two pop-ups).
///
/// Android 12+ once PROCTOR installed its own current version: no pop-up at all. The update is
/// downloaded and verified in the background, then installed by itself once the teacher has left
/// the app for <see cref="IdleBeforeInstall"/>, and never while sharing their screen or controlling
/// a PC, so it can't cut a lesson short. Android replaces the app; the next time it's opened it's
/// the new version.
/// </summary>
public static class UpdateNotifier
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private static readonly TimeSpan RetryWhileBusy = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdleBeforeInstall = TimeSpan.FromMinutes(1);

    private static int _started;
    private static AppRelease? _pending;

    // Silent path: the verified package waiting for the app to be idle in the background.
    private static AppRelease? _readyRelease;
    private static string? _readyPath;
    private static CancellationTokenSource? _idle;
    private static int _installing;
    private static volatile bool _inBackground;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(LoopAsync);
    }

    private static async Task LoopAsync()
    {
        await Task.Delay(FirstCheck);
        while (true)
        {
            try
            {
                var release = await AppUpdater.GetLatestAsync(CancellationToken.None);
                if (release is { IsNewer: true })
                {
                    if (CanUpdateSilently && release.SetupUrl is not null)
                        await DownloadQuietlyAsync(release);
                    else if (!AppUpdater.IsSnoozed(release))
                        _pending = release;
                }
            }
            catch { /* offline: try again next round */ }

            // Offer it as soon as the app isn't locked (re-checked every minute until shown).
            var waited = TimeSpan.Zero;
            while (_pending is not null && waited < Interval)
            {
                var shown = await MainThread.InvokeOnMainThreadAsync(() => TryShowAsync(_pending));
                if (shown) { _pending = null; break; }
                await Task.Delay(RetryWhileBusy);
                waited += RetryWhileBusy;
            }
            await Task.Delay(Interval - waited > TimeSpan.Zero ? Interval - waited : RetryWhileBusy);
        }
    }

    private static bool CanUpdateSilently => PlatformServices.CanUpdateSilently?.Invoke() == true;

    private static async Task DownloadQuietlyAsync(AppRelease release)
    {
        if (_readyRelease?.Version == release.Version && _readyPath is not null && File.Exists(_readyPath)) return;
        try
        {
            var path = await AppUpdater.DownloadSetupAsync(release, new Progress<double>(), CancellationToken.None);
            _readyPath = path;
            _readyRelease = release;
            if (_inBackground) OnAppStopped();   // already away from the app: start the idle wait now
        }
        catch
        {
            // Couldn't download now: the next round tries again; meanwhile offer the pop-up.
            if (!AppUpdater.IsSnoozed(release)) _pending = release;
        }
    }

    /// <summary>The app went to the background (another app, home screen, screen off).</summary>
    public static void OnAppStopped()
    {
        _inBackground = true;
        if (_readyPath is null) return;
        _idle?.Cancel();
        var idle = _idle = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(IdleBeforeInstall, idle.Token); }
            catch (OperationCanceledException) { return; }
            await InstallIfIdleAsync();
        });
    }

    /// <summary>The teacher is back in the app: don't install under their hands.</summary>
    public static void OnAppResumed()
    {
        _inBackground = false;
        _idle?.Cancel();
        // Android needed a tap after all (it couldn't show its prompt while the app was away):
        // offer the update the usual way.
        if (_readyRelease is { } r && Volatile.Read(ref _installing) == 1)
        {
            Interlocked.Exchange(ref _installing, 0);
            _pending = r;
            _ = MainThread.InvokeOnMainThreadAsync(() => TryShowAsync(r));
        }
    }

    private static async Task InstallIfIdleAsync()
    {
        if (_readyPath is not { } path || !File.Exists(path) || PlatformServices.InstallPackage is not { } install) return;
        if (IsBusy) { OnAppStopped(); return; }   // sharing or in control: look again in a minute
        if (Interlocked.Exchange(ref _installing, 1) == 1) return;
        try
        {
            // On success Android stops this app and replaces it: nothing runs after this.
            if (await MainThread.InvokeOnMainThreadAsync(() => install(path)) is not null)
                Interlocked.Exchange(ref _installing, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    /// <summary>Showing a screen to the class or controlling a PC: no automatic install now.</summary>
    private static bool IsBusy
    {
        get
        {
            if (RemoteControlSession.AnyActive) return true;
            var vm = IPlatformApplication.Current?.Services.GetService(typeof(DashboardViewModel)) as DashboardViewModel;
            return vm?.Broadcaster.IsActive == true;
        }
    }

    /// <summary>Show the pop-up for <paramref name="release"/> unless the app is locked or one is already open.</summary>
    public static async Task<bool> TryShowAsync(AppRelease release)
    {
        var navigation = Shell.Current?.Navigation ?? Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation;
        if (navigation is null) return false;
        var top = navigation.ModalStack.LastOrDefault();
        if (top is LockPage) return false;                 // after the teacher unlocks
        if (top is UpdatePage) return true;                // already on screen
        await navigation.PushModalAsync(new UpdatePage(release));
        return true;
    }
}
