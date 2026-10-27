using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Samsung;
using Foundation;
using UIKit;
using WebKit;

namespace Apps2Samsung.Mobile.Platforms.iOS;

// Hosts the Samsung SignInGate in a WKWebView and captures the token it POSTs to the redirect_uri
// through the shared SamsungCallbackListener. iOS counterpart of Platforms/Android/SamsungLoginActivity:
// same flow, same listener, only the WebView hosting differs. "Sign in with Google" on the gate can't
// work in here either (see SamsungOAuth.IsGoogleSignIn): that navigation is stopped and a notice tells
// the user to add a Samsung account password.
internal sealed class SamsungLoginViewController : UIViewController
{
    private readonly TaskCompletionSource<string> _result;
    private readonly CancellationTokenSource _cts;
    private WKWebView? _web;
    private SamsungCallbackListener? _listener;

    public SamsungLoginViewController(TaskCompletionSource<string> result, CancellationToken cancellation)
    {
        _result = result;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        Title = "Samsung";
        // The system Cancel item: localized by iOS, no string of our own needed.
        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Cancel, (_, _) => Cancel());

        var config = new WKWebViewConfiguration();
        config.Preferences.JavaScriptCanOpenWindowsAutomatically = true;   // SignInGate opens windows

        _web = new WKWebView(View!.Bounds, config)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            // Samsung's account pages serve a broken flow to unknown/embedded UAs; present as Safari.
            CustomUserAgent =
                "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
                "Version/17.5 Mobile/15E148 Safari/604.1",
            UIDelegate = new PopupsInSameViewDelegate(BlocksGoogleSignIn),
            NavigationDelegate = new MainFrameFailureDelegate(msg => Fault(new InvalidOperationException(msg)), BlocksGoogleSignIn),
        };
        View.AddSubview(_web);

        _ = Task.Run(() => ListenForCallbackAsync(_cts.Token));

        _web.LoadRequest(new NSUrlRequest(new NSUrl(SamsungOAuth.BuildAuthorizeUrl(SamsungCallbackListener.RedirectUri))));
    }

    private async Task ListenForCallbackAsync(CancellationToken ct)
    {
        try
        {
            using var listener = new SamsungCallbackListener();
            _listener = listener;
            var tokenJson = await listener.WaitForTokenAsync(ct);
            InvokeOnMainThread(() =>
            {
                _result.TrySetResult(tokenJson);
                DismissViewController(true, null);
            });
        }
        catch (OperationCanceledException) { /* controller closing */ }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            _listener = null;
        }
    }

    // True when the navigation was stopped because it leads to Google's sign-in page, in which case the
    // notice is up (once) and the login page stays where it is for the email + password route.
    private bool BlocksGoogleSignIn(string? url)
    {
        if (!SamsungOAuth.IsGoogleSignIn(url))
            return false;

        if (PresentedViewController is null)
        {
            var notice = UIAlertController.Create(
                L10n.Get("lblGoogleSignInUnavailable"), L10n.Get("statusGoogleSignInUnavailable"), UIAlertControllerStyle.Alert);
            notice.AddAction(UIAlertAction.Create(L10n.Get("lblOk"), UIAlertActionStyle.Default, null));
            PresentViewController(notice, true, null);
        }
        return true;
    }

    private void Cancel()
    {
        _result.TrySetCanceled();
        DismissViewController(true, null);
    }

    private void Fault(Exception ex) => InvokeOnMainThread(() =>
    {
        _result.TrySetException(ex);
        DismissViewController(true, null);
    });

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        // Leaving without a captured token (Cancel, or dismissed from outside) unblocks the awaiter;
        // after a result these are no-ops.
        _cts.Cancel();
        _result.TrySetCanceled();
        _listener?.Dispose();
    }

    // SignInGate's window.open() calls: WKWebView gives us no window to open, so load the request in
    // the same view instead of dropping it (the Android side's PopupsInSameViewClient) — unless it is
    // the Google sign-in, which becomes the notice.
    private sealed class PopupsInSameViewDelegate(Func<string?, bool> blocksGoogleSignIn) : WKUIDelegate
    {
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration,
            WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            if (blocksGoogleSignIn(navigationAction.Request.Url?.AbsoluteString))
                return null;
            if (navigationAction.TargetFrame is null || !navigationAction.TargetFrame.MainFrame)
                webView.LoadRequest(navigationAction.Request);
            return null;
        }
    }

    // Stops navigations to Google's sign-in page, and surfaces main-frame load failures with their
    // real reason (the Android LoggingClient). Redirects cancel the in-flight load, which WebKit
    // reports as an error too; that one is not a failure.
    private sealed class MainFrameFailureDelegate(Action<string> fault, Func<string?, bool> blocksGoogleSignIn) : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction,
            Action<WKNavigationActionPolicy> decisionHandler)
            => decisionHandler(blocksGoogleSignIn(navigationAction.Request.Url?.AbsoluteString)
                ? WKNavigationActionPolicy.Cancel
                : WKNavigationActionPolicy.Allow);

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error)
        {
            if (error.Code == (nint)(long)NSUrlError.Cancelled)
                return;
            fault($"load error {error.Code}: {error.LocalizedDescription} @ {webView.Url}");
        }
    }
}
