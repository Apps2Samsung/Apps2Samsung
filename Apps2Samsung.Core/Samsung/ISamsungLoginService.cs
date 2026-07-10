using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Models;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Performs the Samsung account OAuth sign-in and returns the resulting token bundle
    /// (access token, user id, email) needed to provision the Tizen author/distributor
    /// certificates. Each head implements it its own way: the desktop opens the system
    /// browser against a loopback Kestrel callback; the mobile head hosts a WebView with an
    /// in-app loopback listener. Samsung's SignInGate POSTs the token to the redirect_uri, so
    /// the callback must read a POST body — not a URL query string.
    /// </summary>
    public interface ISamsungLoginService
    {
        Task<SamsungAuth> LoginAsync(CancellationToken cancellationToken = default);
    }
}
