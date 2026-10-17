using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Samsung;
using AndroidUri = Android.Net.Uri;
using AndroidWebView = Android.Webkit.WebView;

namespace Apps2Samsung.Mobile.Platforms.Android;

// Hosts the Samsung SignInGate in a WebView and captures the token it POSTs to the redirect_uri
// through the shared SamsungCallbackListener (a WebView URL-intercept can't read a POST body).
// Proven on-device by the tizen-sdb Probe; this is that recipe wrapped behind SamsungLoginService.
// Platforms/iOS/SamsungLoginViewController is the same flow over a WKWebView.
[Activity(Label = "Samsung Login", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public class SamsungLoginActivity : Activity
{
    // Single-flight bridge to SamsungLoginService: it sets this before launching, the activity
    // completes it with the raw token JSON (or faults/cancels it).
    internal static TaskCompletionSource<string>? Pending;

    private SamsungCallbackListener? _listener;
    private readonly CancellationTokenSource _cts = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        var web = new AndroidWebView(this);
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

        web.SetWebViewClient(new LoggingClient(msg => Fault(new InvalidOperationException(msg))));

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

    protected override void OnDestroy()
    {
        _cts.Cancel();
        _listener?.Dispose();
        // If we're leaving without a captured token (user backed out), unblock the awaiter.
        Pending?.TrySetCanceled();
        Pending = null;
        base.OnDestroy();
    }

    // Keeps navigation inside the WebView and surfaces main-frame load failures (so a failed page
    // gives the real reason rather than a generic error screen).
    private sealed class LoggingClient(Action<string> log) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(AndroidWebView? view, IWebResourceRequest? request)
            => false; // let the WebView handle every navigation (incl. the localhost POST)

        public override void OnReceivedError(AndroidWebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame == true)
                log($"load error {(int?)error?.ErrorCode}: {error?.Description} @ {request?.Url}");
            base.OnReceivedError(view, request, error);
        }
    }
}
