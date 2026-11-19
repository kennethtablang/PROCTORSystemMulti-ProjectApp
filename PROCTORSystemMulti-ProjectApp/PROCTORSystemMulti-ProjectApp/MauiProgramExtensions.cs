using Microsoft.Extensions.Logging;
using PROCTORSystemMulti_ProjectApp.PROCTOR;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace PROCTORSystemMulti_ProjectApp
{
    public static class MauiProgramExtensions
    {
        public static MauiAppBuilder UseSharedMauiApp(this MauiAppBuilder builder)
        {
            builder
                .UseMauiApp<App>()
                .UseSkiaSharp() // enables SKCanvasView (focus-view live video) on every head
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    // Toolbar icons on Android/iOS/Mac (Windows uses its built-in Segoe MDL2 Assets).
                    fonts.AddFont("MaterialIcons-Regular.ttf", "MaterialIcons");
                });

            // Every button animates when pressed (shrinks and dims, then springs back).
            Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("PressAnimation", (_, view) =>
            {
                if (view is Button button) PressAnimation.Attach(button);
            });

            // One shared view model (FR-TEACH-03): a single server connection and a single live
            // roster behind the dashboard and every student view. MAUI resolves Shell content pages
            // through this container, so the singleton is injected into the dashboard.
            // Screen capture for "show my screen" (FR-CTRL-02): heads that support it register their own
            // IScreenSource after this (the last registration wins); the rest get the unsupported stub.
            builder.Services.AddSingleton<IScreenSource, UnsupportedScreenSource>();
            builder.Services.AddSingleton<DashboardViewModel>(sp => new DashboardViewModel(sp.GetRequiredService<IScreenSource>()));
            builder.Services.AddTransient<DashboardPage>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder;
        }
    }
}
