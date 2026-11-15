using Android.App;
using Android.Net.Wifi;
using Android.Runtime;

namespace PROCTORSystemMulti_ProjectApp.Droid
{
    [Application]
    public class MainApplication : MauiApplication
    {
        private WifiManager.MulticastLock? _multicastLock;

        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        public override void OnCreate()
        {
            base.OnCreate();

            // Android Wi-Fi filters broadcast/multicast frames to save power unless an app holds a
            // multicast lock. Without it the server's UDP discovery replies and beacons never reach
            // the teacher app (FR-DISC-04); the manual-address fallback would still work.
            try
            {
                var wifi = (WifiManager?)GetSystemService(WifiService);
                _multicastLock = wifi?.CreateMulticastLock("ClassInsightDiscovery");
                _multicastLock?.SetReferenceCounted(false);
                _multicastLock?.Acquire();
            }
            catch (Java.Lang.SecurityException)
            {
                // Permission stripped by an MDM profile: discovery degrades to manual address entry.
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
