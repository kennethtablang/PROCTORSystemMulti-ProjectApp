namespace PROCTORSystemMulti_ProjectApp.WinUI
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseSharedMauiApp();

            // Windows can capture the teacher's screen for "show my screen" (FR-CTRL-02).
            builder.Services.AddSingleton<PROCTORSystemMulti_ProjectApp.ClassInsight.IScreenSource, WindowsScreenSource>();

            return builder.Build();
        }
    }
}
