namespace PROCTORSystemMulti_ProjectApp.PROCTOR;

/// <summary>
/// Checks GitHub for a newer PROCTOR shortly after the app opens and then every few hours while it
/// is in use, and shows the update pop-up on top of whatever page the teacher is on. It waits
/// while the app is PIN-locked, and never stacks two pop-ups.
/// </summary>
public static class UpdateNotifier
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private static readonly TimeSpan RetryWhileBusy = TimeSpan.FromMinutes(1);

    private static int _started;
    private static AppRelease? _pending;

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
                if (release is { IsNewer: true } && !AppUpdater.IsSnoozed(release)) _pending = release;
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
