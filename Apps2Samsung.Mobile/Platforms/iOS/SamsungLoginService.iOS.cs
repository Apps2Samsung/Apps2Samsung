using Apps2Samsung.Mobile.Platforms.iOS;
using Microsoft.Maui.ApplicationModel;
using UIKit;

namespace Apps2Samsung.Mobile.Services;

public sealed partial class SamsungLoginService
{
    // Presents SamsungLoginViewController (WKWebView + loopback listener) modally over the current
    // page and awaits the token it captures. UIKit objects must be created on the main thread.
    private partial Task<string> CaptureTokenAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var host = Platform.GetCurrentUIViewController()
                    ?? throw new InvalidOperationException("No current view controller to present the Samsung login from.");

                var login = new SamsungLoginViewController(tcs, cancellationToken);
                // Full screen rather than a sheet: a swipe-down would dismiss the page mid-login
                // without going through the Cancel button.
                var nav = new UINavigationController(login) { ModalPresentationStyle = UIModalPresentationStyle.FullScreen };
                host.PresentViewController(nav, true, null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }
}
