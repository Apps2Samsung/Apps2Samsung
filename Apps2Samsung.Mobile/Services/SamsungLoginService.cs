using System.Text.Json;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Mobile <see cref="ISamsungLoginService"/>: shows the Samsung SignInGate in a platform WebView next
/// to an in-app loopback listener (<see cref="SamsungCallbackListener"/>), awaits the token the gate
/// POSTs there, and deserializes that URL-encoded JSON into a <see cref="SamsungAuth"/>.
/// <para>
/// The WebView hosting is the only platform-specific part: <c>Platforms/Android</c> runs it as an
/// Activity, <c>Platforms/iOS</c> as a modal view controller. Both implement
/// <see cref="CaptureTokenAsync"/>.
/// </para>
/// </summary>
public sealed partial class SamsungLoginService : ISamsungLoginService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SamsungAuth> LoginAsync(CancellationToken cancellationToken = default)
    {
        var tokenJson = await CaptureTokenAsync(cancellationToken);

        return JsonSerializer.Deserialize<SamsungAuth>(tokenJson, JsonOptions)
            ?? throw new InvalidOperationException("Samsung login returned an unparseable token.");
    }

    /// <summary>Shows the sign-in UI and completes with the raw token JSON, or cancels/faults.</summary>
    private partial Task<string> CaptureTokenAsync(CancellationToken cancellationToken);
}
