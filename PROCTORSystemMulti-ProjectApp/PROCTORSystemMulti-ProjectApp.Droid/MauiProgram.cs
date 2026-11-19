namespace PROCTORSystemMulti_ProjectApp.Droid
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseSharedMauiApp();

            // Android can share the teacher's screen with the class (FR-CTRL-02), like Windows...
            builder.Services.AddSingleton<PROCTORSystemMulti_ProjectApp.PROCTOR.IScreenSource, AndroidScreenSource>();

            // ...and control a student's PC by touch, mouse and keyboard (FR-CTRL-05).
            PROCTORSystemMulti_ProjectApp.PROCTOR.PlatformServices.RemoteInput = new AndroidRemoteInputCapture();

            // Auto-update: install a newer PROCTOR-Teacher-Android.apk from the GitHub release.
            PROCTORSystemMulti_ProjectApp.PROCTOR.PlatformServices.InstallPackage = UpdateInstaller.InstallAsync;
            // Android 12+: once PROCTOR installed itself, updates install with no one tapping anything.
            PROCTORSystemMulti_ProjectApp.PROCTOR.PlatformServices.CanUpdateSilently = UpdateInstaller.CanUpdateSilently;

            return builder.Build();
        }
    }
}
