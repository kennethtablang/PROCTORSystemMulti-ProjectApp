using Android.App;
using Android.Content;
using Android.Content.PM;

namespace PROCTORSystemMulti_ProjectApp.Droid;

/// <summary>
/// Android auto-update (the Windows app runs its setup instead). The app downloads the release's
/// PROCTOR-Teacher-Android.apk, checks its size and SHA-256 and installs it here through Android's
/// PackageInstaller. Android accepts it only when it's signed with the same key as this app, so a
/// tampered file can't replace it.
///
/// Whether anyone has to tap:
///  * Android 12+ when PROCTOR installed its current version itself (it is the "installer of
///    record"): no. The update is installed silently (USER_ACTION_NOT_REQUIRED).
///  * The first update after installing the .apk by hand, or Android 11 and older: Android asks
///    once, "Do you want to update this app?". After that first in-app update, PROCTOR is its own
///    installer of record, so later updates are silent on Android 12+.
///  * Once ever: Android asks to allow PROCTOR to install apps ("Install unknown apps").
/// </summary>
public static class UpdateInstaller
{
    private const int UserActionNotRequired = 2;   // PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED (API 31)

    /// <summary>Android 12+, PROCTOR installed itself, and may install apps: updates need no tap.</summary>
    public static bool CanUpdateSilently()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return false;
        try
        {
            var context = Android.App.Application.Context;
            var pm = context.PackageManager;
            if (pm is null || !pm.CanRequestPackageInstalls()) return false;
            var source = pm.GetInstallSourceInfo(context.PackageName ?? "");
            return source.InstallingPackageName == context.PackageName;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> InstallAsync(string apkPath)
    {
        var context = Platform.CurrentActivity ?? (Context)Android.App.Application.Context;

        if (OperatingSystem.IsAndroidVersionAtLeast(26) && context.PackageManager?.CanRequestPackageInstalls() == false)
        {
            var settings = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + context.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settings);
            return "Allow PROCTOR to install updates: turn on \"Allow from this source\" on the screen that opened, " +
                   "come back, then tap Install. (Once only: later updates install by themselves.)";
        }

        var installer = context.PackageManager?.PackageInstaller ?? throw new InvalidOperationException("Android's installer isn't available.");
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        parameters.SetAppPackageName(context.PackageName);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            parameters.SetRequireUserAction(UserActionNotRequired);

        var sessionId = installer.CreateSession(parameters);
        using var session = installer.OpenSession(sessionId);
        try
        {
            await using (var input = File.OpenRead(apkPath))
            await using (var output = session.OpenWrite("PROCTOR-Teacher-Android.apk", 0, input.Length))
            {
                await input.CopyToAsync(output);
                session.Fsync(output);
            }

            // Android reports the result (or "needs the user to confirm") to UpdateStatusReceiver.
            var status = new Intent(context, typeof(UpdateStatusReceiver));
            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(31)) flags |= PendingIntentFlags.Mutable;   // the installer adds the result extras
            var pending = PendingIntent.GetBroadcast(context, sessionId, status, flags)
                ?? throw new InvalidOperationException("Couldn't prepare the install.");
            session.Commit(pending.IntentSender);
            return null;
        }
        catch
        {
            session.Abandon();
            throw;
        }
    }
}

/// <summary>
/// Android's answer to an update install. "Needs the user": open Android's confirmation (only
/// possible while PROCTOR is on screen; otherwise the app offers the update again when reopened).
/// Success: Android has already replaced the app.
/// </summary>
[BroadcastReceiver(Exported = false)]
public sealed class UpdateStatusReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        var status = intent.GetIntExtra(PackageInstaller.ExtraStatus, int.MinValue);
        if (status == (int)PackageInstallStatus.PendingUserAction)
        {
#pragma warning disable CA1422, CS0618 // typed overload is API 33+; this one works on every version
            if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
#pragma warning restore CA1422, CS0618
            {
                confirm.AddFlags(ActivityFlags.NewTask);
                try { context.StartActivity(confirm); } catch { }
            }
        }
        else if (status != (int)PackageInstallStatus.Success)
        {
            Android.Util.Log.Warn("PROCTOR", $"Update not installed ({status}): {intent.GetStringExtra(PackageInstaller.ExtraStatusMessage)}");
        }
    }
}
