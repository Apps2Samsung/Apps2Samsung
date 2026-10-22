using System;
using System.Net.Http;
using System.Net.Sockets;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Recognises a download that failed because of the user's connection rather than the app, so
    /// both heads can say so in plain words instead of showing the exception (#809: an HttpClient
    /// timeout reached the desktop's error dialog as a full stack trace).
    /// </summary>
    public static class NetworkFailure
    {
        /// <summary>
        /// The en.json key that explains <paramref name="ex"/>, or null when it isn't a connection
        /// problem. A cancel the user asked for is not one: callers check their own token first.
        /// </summary>
        public static string? LocalizationKey(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                // HttpClient.Timeout elapsing: a TaskCanceledException around a TimeoutException.
                if (e is TimeoutException)
                    return "errNetworkTimeout";

                // No route, refused, DNS failure, connection reset: all surface as a socket error
                // inside the HttpRequestException.
                if (e is SocketException)
                    return "errNetworkUnreachable";
            }

            return ex is HttpRequestException { StatusCode: null } ? "errNetworkUnreachable" : null;
        }
    }
}
