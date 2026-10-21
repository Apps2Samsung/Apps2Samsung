using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Samsung;
using AndroidUri = Android.Net.Uri;
using AndroidWebView = Android.Webkit.WebView;

namespace Apps2Samsung.Mobile.Platforms.Android;

// Hosts the Samsung SignInGate in a WebView and captures the token it POSTs to the redirect_uri
// through the shared SamsungCallbackListener (a WebView URL-intercept can't read a POST body).
// Proven on-device by the tizen-sdb Probe; this is that recipe wrapped behind SamsungLoginService.
// Platforms/iOS/SamsungLoginViewController is the same flow over a WKWebView.
//
// "Sign in with Google" on the gate can't work in here (see SamsungOAuth.IsGoogleSignIn), so the
// navigation to Google is stopped and a notice tells the user to add a Samsung account password.
[Activity(Label = "Samsung Login", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public class SamsungLoginActivity : Activity
{
    // Single-flight bridge to SamsungLoginService: it sets this before launching, the activity
    // completes it with the raw token JSON (or faults/cancels it).
    internal static TaskCompletionSource<string>? Pending;

    private SamsungCallbackListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private AndroidWebView? _web;
    private AlertDialog? _googleNotice;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var web = new AndroidWebView(this);
        _web = web;
        web.Settings.JavaScriptEnabled = true;   // SignInGate needs JS
        web.Settings.DomStorageEnabled = true;
        web.Settings.DatabaseEnabled = true;
        web.Settings.JavaScriptCanOpenWindowsAutomatically = true;
        web.Settings.SetSupportMultipleWindows(true);
        // The final redirect goes to http://localhost from an https page — allow it.
        web.Settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
        // Samsung's account pages behave badly under the default "; wv" WebView UA.
        web.Settings.UserAgentString =
            "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/126.0.0.0 Mobile Safari/537.36";

        // Cross-domain login needs cookies, including third-party.
        CookieManager.Instance!.SetAcceptCookie(true);
        CookieManager.Instance.SetAcceptThirdPartyCookies(web, true);

        web.SetWebViewClient(new LoggingClient(msg => Fault(new InvalidOperationException(msg)), BlocksGoogleSignIn));
        web.SetWebChromeClient(new PopupsInSameViewClient(OpenPopup));

        SetContentView(web);

        _ = Task.Run(() => ListenForCallbackAsync(_cts.Token));

        web.LoadUrl(SamsungOAuth.BuildAuthorizeUrl(SamsungCallbackListener.RedirectUri));
    }

    private async Task ListenForCallbackAsync(CancellationToken ct)
    {
        try
        {
            using var listener = new SamsungCallbackListener();
            _listener = listener;
            var tokenJson = await listener.WaitForTokenAsync(ct);
            RunOnUiThread(() =>
            {
                Pending?.TrySetResult(tokenJson);
                Pending = null;
                Finish();
            });
        }
        catch (System.OperationCanceledException) { /* activity closing */ }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            _listener = null;
        }
    }

    private void Fault(Exception ex) => RunOnUiThread(() =>
    {
        Pending?.TrySetException(ex);
        Pending = null;
        Finish();
    });

    // The gate opens its social sign-in buttons with window.open(). Google's is a dead end here, so it
    // becomes the notice; anything else loads in the one view we have (the iOS PopupsInSameViewDelegate).
    private void OpenPopup(string url)
    {
        if (!BlocksGoogleSignIn(url))
            _web?.LoadUrl(url);
    }

    // True when the navigation was stopped because it leads to Google's sign-in page, in which case the
    // notice is up (once) and the login page stays where it is for the email + password route.
    private bool BlocksGoogleSignIn(string? url)
    {
        if (!SamsungOAuth.IsGoogleSignIn(url))
            return false;

        if (_googleNotice is null || !_googleNotice.IsShowing)
        {
            _googleNotice = new AlertDialog.Builder(this)
                .SetTitle(L10n.Get("lblGoogleSignInUnavailable"))!
                .SetMessage(L10n.Get("statusGoogleSignInUnavailable"))!
                .SetPositiveButton(L10n.Get("lblOk"), (_, _) => { })!
                .Show();
        }
        return true;
    }

    protected override void OnDestroy()
    {
        _googleNotice?.Dismiss();
        _googleNotice = null;
        _cts.Cancel();
        _listener?.Dispose();
        // If we're leaving without a captured token (user backed out), unblock the awaiter.
        Pending?.TrySetCanceled();
        Pending = null;
        base.OnDestroy();
    }

    // Keeps navigation inside the WebView (incl. the localhost POST) except for Google's sign-in page,
    // and surfaces main-frame load failures (so a failed page gives the real reason rather than a
    // generic error screen).
    private sealed class LoggingClient(Action<string> log, Func<string?, bool> blocksGoogleSignIn) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(AndroidWebView? view, IWebResourceRequest? request)
            => blocksGoogleSignIn(request?.Url?.ToString());

        public override void OnReceivedError(AndroidWebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame == true)
                log($"load error {(int?)error?.ErrorCode}: {error?.Description} @ {request?.Url}");
            base.OnReceivedError(view, request, error);
        }
    }

    // Answers window.open() with a throwaway WebView whose only job is to report the URL the popup
    // wants; the activity then decides where (or whether) it loads. Without a chrome client the gate's
    // popups were dropped silently, which is why the Google button used to "do nothing".
    private sealed class PopupsInSameViewClient(Action<string> open) : WebChromeClient
    {
        public override bool OnCreateWindow(AndroidWebView? view, bool isDialog, bool isUserGesture, Message? resultMsg)
        {
            if (view is null || resultMsg?.Obj is not AndroidWebView.WebViewTransport transport)
                return false;

            var probe = new AndroidWebView(view.Context!);
            probe.Settings.JavaScriptEnabled = true;
            probe.SetWebViewClient(new PopupUrlClient(url =>
            {
                open(url);
                probe.Post(() => probe.Destroy());   // not from inside its own callback
            }));
            transport.WebView = probe;
            resultMsg.SendToTarget();
            return true;
        }

        private sealed class PopupUrlClient(Action<string> report) : WebViewClient
        {
            public override bool ShouldOverrideUrlLoading(AndroidWebView? view, IWebResourceRequest? request)
            {
                if (request?.Url?.ToString() is { Length: > 0 } url && url != "about:blank")
                    report(url);
                return true; // never load anything in the probe itself
            }
        }
    }
}
