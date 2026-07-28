using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Browser.CustomTabs;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apps2Samsung.Samsung;
using AndroidIntent = Android.Content.Intent;
using AndroidUri = Android.Net.Uri;

namespace Apps2Samsung.Mobile.Platforms.Android;

// Runs the Samsung SignInGate in the system browser (Chrome Custom Tab) and captures the token it
// POSTs to the redirect_uri via an in-app loopback listener on :4794 — the same contract the desktop
// head fulfils with Kestrel. An embedded WebView cannot be used here: accounts.google.com rejects
// embedded user agents (disallowed_useragent), which kills SignInGate's "Sign in with Google" path.
[Activity(Label = "Samsung Login",
          LaunchMode = LaunchMode.SingleTask,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public class SamsungLoginActivity : Activity
{
    // Single-flight bridge to SamsungLoginService: it sets this before launching, the activity
    // completes it with the raw token JSON (or faults/cancels it).
    internal static TaskCompletionSource<string>? Pending;

    private const string StateTabLaunched = "tab_launched";

    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private bool _tabLaunched;
    private volatile bool _settled;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ActionBar?.Hide();

        _tabLaunched = savedInstanceState?.GetBoolean(StateTabLaunched) ?? false;

        _ = Task.Run(() => ListenForCallbackAsync(_cts.Token));
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        outState.PutBoolean(StateTabLaunched, _tabLaunched);
        base.OnSaveInstanceState(outState);
    }

    protected override void OnResume()
    {
        base.OnResume();

        // Already resolved (token or error): the CLEAR_TOP intent dismissed the tab and brought
        // us back, so all that's left is to tear down.
        if (_settled)
        {
            Finish();
            return;
        }

        if (!_tabLaunched)
        {
            _tabLaunched = true;
            LaunchTab();
            return;
        }

        // Foreground again with nothing captured: the user dismissed the browser tab.
        Pending?.TrySetCanceled();
        Pending = null;
        Finish();
    }

    private void LaunchTab()
    {
        var redirect = $"http://localhost:{SamsungOAuth.CallbackPort}{SamsungOAuth.CallbackPath}";
        var url = AndroidUri.Parse(SamsungOAuth.BuildAuthorizeUrl(redirect));

        if (url is null)
        {
            Fault(new InvalidOperationException("Could not build the SignInGate authorize URL."));
            return;
        }

        try
        {
            new CustomTabsIntent.Builder()
                .SetShowTitle(true)
                .Build()
                .LaunchUrl(this, url);
        }
        catch (global::Android.Content.ActivityNotFoundException)
        {
            // No Custom Tabs provider — fall back to whatever browser is installed.
            try
            {
                StartActivity(new AndroidIntent(AndroidIntent.ActionView, url));
            }
            catch (global::Android.Content.ActivityNotFoundException ex)
            {
                Fault(new InvalidOperationException(
                    "No browser is available to complete the Samsung login.", ex));
            }
        }
    }

    private async Task ListenForCallbackAsync(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, SamsungOAuth.CallbackPort);
            _listener.Start();

            while (!ct.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();

                var (method, path, body) = await ReadRequestAsync(stream, ct);

                // Briefly visible in the browser tab before CLEAR_TOP dismisses it.
                const string page =
                    "<!doctype html><meta name=viewport content=\"width=device-width,initial-scale=1\">" +
                    "<body style=\"font:16px system-ui;padding:2rem\">" +
                    "Login captured — returning to Apps2Samsung…</body>";
                var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                           $"Content-Length: {Encoding.UTF8.GetByteCount(page)}\r\n" +
                           "Connection: close\r\n\r\n" + page;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct);
                await stream.FlushAsync(ct);

                if (method == "POST" && path.StartsWith(SamsungOAuth.CallbackPath, StringComparison.Ordinal))
                {
                    // Reject a callback whose state doesn't round-trip (CSRF / stray callback).
                    var state = ParseFormField(body, "state");
                    if (!SamsungOAuth.IsValidState(state is null ? null : Uri.UnescapeDataString(state)))
                    {
                        Fault(new InvalidOperationException("Samsung login state mismatch — aborting."));
                        return;
                    }

                    var code = ParseFormField(body, "code");
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        var tokenJson = Uri.UnescapeDataString(code);
                        RunOnUiThread(() => Complete(tokenJson));
                        return;
                    }
                }
            }
        }
        catch (System.OperationCanceledException) { /* activity closing */ }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            try { _listener?.Stop(); } catch { /* ignore */ }
        }
    }

    private void Complete(string tokenJson)
    {
        _settled = true;
        Pending?.TrySetResult(tokenJson);
        Pending = null;
        BringToFront();
    }

    private void Fault(Exception ex) => RunOnUiThread(() =>
    {
        _settled = true;
        Pending?.TrySetException(ex);
        Pending = null;
        BringToFront();
    });

    // Pops the browser tab off this task and re-enters OnResume, which finishes the activity.
    // Needed because the tab lives in our task and there is no redirect Intent to close it —
    // the callback arrives over a socket, not an intent filter.
    private void BringToFront() =>
        StartActivity(new AndroidIntent(this, typeof(SamsungLoginActivity))
            .AddFlags(global::Android.Content.ActivityFlags.ClearTop | global::Android.Content.ActivityFlags.SingleTop));

    protected override void OnDestroy()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }

        // Only unblock the awaiter if we're leaving without having resolved it.
        if (!_settled)
        {
            Pending?.TrySetCanceled();
            Pending = null;
        }

        _cts.Dispose();
        base.OnDestroy();
    }

    // Minimal HTTP/1.1 request reader: request line + headers, then the Content-Length body.
    private static async Task<(string method, string path, string body)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        int headerEnd;

        while ((headerEnd = sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        }

        var raw = sb.ToString();
        headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = headerEnd >= 0 ? raw[..headerEnd] : raw;
        var lines = head.Split("\r\n");
        var requestLine = lines.Length > 0 ? lines[0].Split(' ') : new[] { "", "", "" };
        var method = requestLine.Length > 0 ? requestLine[0] : "";
        var path = requestLine.Length > 1 ? requestLine[1] : "";

        int contentLength = 0;
        foreach (var line in lines)
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out contentLength);

        var body = headerEnd >= 0 ? raw[(headerEnd + 4)..] : string.Empty;
        while (Encoding.UTF8.GetByteCount(body) < contentLength)
        {
            int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
            if (n <= 0) break;
            body += Encoding.UTF8.GetString(buf, 0, n);
        }

        return (method, path, body);
    }

    private static string? ParseFormField(string body, string key)
    {
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return kv[1];
        }
        return null;
    }
}
