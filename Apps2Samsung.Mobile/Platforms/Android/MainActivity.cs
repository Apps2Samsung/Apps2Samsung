using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile;

// apps2samsung:// install links (#1066): a browser or another app hands them to this activity.
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "apps2samsung")]
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        InstallLinkInbox.Deliver(Intent?.DataString);
    }

    // SingleTop: a link while the app runs comes here instead of a new activity.
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        InstallLinkInbox.Deliver(intent?.DataString);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        LocalFilePicker.OnActivityResult(requestCode, resultCode, data);
    }
}
