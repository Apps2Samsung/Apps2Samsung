using Android.Content;
using Apps2Samsung.Mobile.Platforms.Android;
using Microsoft.Maui.ApplicationModel;

namespace Apps2Samsung.Mobile.Services;

public sealed partial class SamsungLoginService
{
    // Launches SamsungLoginActivity (WebView + loopback listener) and awaits the token it captures.
    private partial Task<string> CaptureTokenAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SamsungLoginActivity.Pending = tcs;

        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("No current activity to launch the Samsung login from.");
        activity.StartActivity(new Intent(activity, typeof(SamsungLoginActivity)));

        return tcs.Task;
    }
}
