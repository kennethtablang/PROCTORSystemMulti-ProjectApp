using Microsoft.Extensions.Logging;
using PROCTORSystemMulti_ProjectApp.ClassInsight;
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
                });

            // One shared view model backs every navbar tab (FR-TEACH-03): a single server
            // connection and a single live roster, so the Dashboard, Processes and Bandwidth
            // views all show the same students updating together. MAUI resolves Shell content
            // pages through this container, so the singleton is injected into each page.
            // Screen capture for "show my screen" (FR-CTRL-02): heads that support it register their own
            // IScreenSource after this (the last registration wins); the rest get the unsupported stub.
            builder.Services.AddSingleton<IScreenSource, UnsupportedScreenSource>();
            builder.Services.AddSingleton<DashboardViewModel>(sp => new DashboardViewModel(sp.GetRequiredService<IScreenSource>()));
            builder.Services.AddTransient<DashboardPage>();
            builder.Services.AddTransient<ProcessesPage>();
            builder.Services.AddTransient<BandwidthPage>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder;
        }
    }
}
