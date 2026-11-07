using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Remote;

namespace Apps2Samsung.TvStore
{
    /// <summary>What a hand-off left on the TV: the upload, and whether TV-Store was brought to the front.</summary>
    public sealed record TvStoreHandoffResult(TvStoreUploadResult Upload, bool TvStoreOpened);

    /// <summary>
    /// The way in without SDB (#979): a package goes to TV-Store's upload page on the TV, TV-Store
    /// comes to the front and asks there, and the person at the TV presses Install (TV-Store signs
    /// it with its own certificate). Nothing is installed from here. One flow for the CLI, the
    /// desktop and the phone; the package arrives customized (the values asked before install) but
    /// not renamed or signed, which is the caller's step before <see cref="SendAsync"/>.
    /// </summary>
    public static class TvStoreHandoff
    {
        private static readonly TimeSpan LaunchPoll = TimeSpan.FromSeconds(2);
        private const int LaunchPolls = 10;

        /// <summary>
        /// True when TV-Store's upload page answers: right away when TV-Store is running, else after
        /// opening it with the TV's own app launch (no SDB) and waiting for its service, up to about
        /// twenty seconds. False when TV-Store is not on the TV or would not start.
        /// <paramref name="progress"/> gets en.json keys.
        /// </summary>
        public static async Task<bool> EnsureRunningAsync(string tvIp, Action<string>? progress = null, CancellationToken ct = default, int port = TvStoreUpload.Port)
        {
            if (await TvStoreUpload.IsAvailableAsync(tvIp, ct, port).ConfigureAwait(false))
                return true;

            progress?.Invoke("statusOpeningTvStore");
            try
            {
                var launch = await SamsungRemoteApps.LaunchAsync(null, tvIp, TvStorePackage.AppId, cancellationToken: ct).ConfigureAwait(false);
                if (!launch.Succeeded || launch.NotInstalled)
                    return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }

            for (var i = 0; i < LaunchPolls; i++)
            {
                await Task.Delay(LaunchPoll, ct).ConfigureAwait(false);
                if (await TvStoreUpload.IsAvailableAsync(tvIp, ct, port).ConfigureAwait(false))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Sends <paramref name="packagePath"/> to TV-Store on <paramref name="tvIp"/> and brings
        /// TV-Store to the front, so its question is on the screen. The upload page must answer
        /// (see <see cref="EnsureRunningAsync"/>).
        /// </summary>
        /// <exception cref="TvStoreUploadException">TV-Store refused the file or could not be reached.</exception>
        public static async Task<TvStoreHandoffResult> SendAsync(string tvIp, string packagePath, Action<string>? progress = null, CancellationToken ct = default, int port = TvStoreUpload.Port)
        {
            progress?.Invoke(Localization.ProgressText.Key("statusSendingToTvStore", Path.GetFileName(packagePath)));
            var upload = await TvStoreUpload.SendAsync(tvIp, packagePath, ct, port).ConfigureAwait(false);

            // TV-Store asks on its own screen; bring it to the front so the question is there. The
            // file waits either way, so a launch that fails is not a failed hand-off.
            var opened = false;
            try
            {
                opened = (await SamsungRemoteApps.LaunchAsync(null, tvIp, TvStorePackage.AppId, cancellationToken: ct).ConfigureAwait(false)).Succeeded;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The file waits anyway.
            }
            return new TvStoreHandoffResult(upload, opened);
        }
    }
}
