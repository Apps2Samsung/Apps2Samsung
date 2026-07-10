using System.Text.Json;
using Android.Content;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Platforms.Android;
using Apps2Samsung.Models;
using Microsoft.Maui.ApplicationModel;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Mobile <see cref="ISamsungLoginService"/>: launches <see cref="SamsungLoginActivity"/> (WebView +
/// in-app loopback listener) and awaits the token it captures, deserializing SignInGate's URL-encoded
/// JSON body into a <see cref="SamsungAuth"/>.
/// </summary>
public sealed class SamsungLoginService : ISamsungLoginService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SamsungAuth> LoginAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SamsungLoginActivity.Pending = tcs;

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("No current activity to launch the Samsung login from.");
        activity.StartActivity(new Intent(activity, typeof(SamsungLoginActivity)));

        var tokenJson = await tcs.Task;

        return JsonSerializer.Deserialize<SamsungAuth>(tokenJson, JsonOptions)
            ?? throw new InvalidOperationException("Samsung login returned an unparseable token.");
    }
}
