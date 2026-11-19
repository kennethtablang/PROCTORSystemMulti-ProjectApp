using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.OS;

namespace PROCTORSystemMulti_ProjectApp.Droid;

/// <summary>
/// Android only lets an app record the screen from a foreground service of type "mediaProjection",
/// which shows a notification while it runs. This service exists for that: it starts in the
/// foreground, turns the teacher's consent into a <see cref="MediaProjection"/> and hands it to
/// <see cref="AndroidScreenSource"/>. Stopping the service ends the recording and the notification.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class ScreenCaptureService : Service
{
    internal const string ExtraResultCode = "proctor.resultCode";
    internal const string ExtraResultData = "proctor.resultData";
    private const string Channel = "proctor.screen";
    private const int NotificationId = 4711;

    /// <summary>Set when the projection is ready (or null when it couldn't start).</summary>
    internal static event Action<MediaProjection?>? ProjectionStarted;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        MediaProjection? projection = null;
        try
        {
            StartAsForeground();
            var code = intent?.GetIntExtra(ExtraResultCode, 0) ?? 0;
#pragma warning disable CA1422, CS0618 // typed overload is API 33+; this one works on every version
            var data = intent?.GetParcelableExtra(ExtraResultData) as Intent;
#pragma warning restore CA1422, CS0618
            var manager = (MediaProjectionManager?)GetSystemService(MediaProjectionService);
            if (data is not null) projection = manager?.GetMediaProjection(code, data);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("PROCTOR", $"Screen sharing couldn't start: {ex}");
        }

        ProjectionStarted?.Invoke(projection);
        if (projection is null) StopSelf();
        return StartCommandResult.NotSticky;
    }

    private void StartAsForeground()
    {
        Notification.Builder builder;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var nm = (NotificationManager?)GetSystemService(NotificationService);
            nm?.CreateNotificationChannel(new NotificationChannel(Channel, "Showing your screen", NotificationImportance.Low));
            builder = new Notification.Builder(this, Channel);
        }
        else
        {
#pragma warning disable CA1422, CS0618
            builder = new Notification.Builder(this);
#pragma warning restore CA1422, CS0618
        }

        // Tapping the notification brings PROCTOR back (to stop sharing from the dashboard).
        var open = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
        var pending = open is null ? null : PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable);
        var notification = builder
            .SetContentTitle("Showing your screen to the class")
            .SetContentText("Stop it from PROCTOR's dashboard.")
            .SetSmallIcon(Android.Resource.Drawable.PresenceVideoOnline)
            .SetOngoing(true)
            .SetContentIntent(pending)
            .Build();

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
        else
            StartForeground(NotificationId, notification);
    }

    public override void OnDestroy()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}
