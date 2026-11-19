using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace PROCTORSystemMulti_ProjectApp.Droid
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        /// <summary>Answers to <see cref="StartActivityForResult(Intent, int)"/> (screen-recording consent).</summary>
        internal static event Action<int, Result, Intent?>? ActivityResult;

        /// <summary>
        /// Hardware keyboard keys, offered first to remote control (FR-CTRL-05). Return true to swallow
        /// the key so it doesn't also act on this app.
        /// </summary>
        internal static Func<KeyEvent, bool>? KeyInterceptor;

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            ActivityResult?.Invoke(requestCode, resultCode, data);
        }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e is not null && KeyInterceptor?.Invoke(e) == true) return true;
            return base.DispatchKeyEvent(e);
        }
    }
}
