using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.TvApp
{
    /// <summary>
    /// Applies the user's TVApp (KaashDev/TVapp) configuration to the package before install:
    /// rewrites the placeholder <c>var channels = [...]</c> array in <c>js/main.js</c>. The actual
    /// wgt-editing lives in the shared <see cref="TvAppChannelInjector"/> (Apps2Samsung.Core); this
    /// desktop patcher just feeds it the channels persisted in <see cref="AppSettings"/>.
    /// (The launcher icon — including the 16:9 "oblong" tile — is handled by
    /// <see cref="CustomIconPackagePatcher"/>.)
    /// </summary>
    public class TvAppPackagePatcher : IPackagePatcher
    {
        public bool CanHandle(string packagePath) => TvAppChannelInjector.AppliesTo(packagePath);

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            var channels = TvAppChannelInjector.ParseChannelsJson(AppSettings.Default.TvAppChannelsJson);
            await TvAppChannelInjector.InjectChannelsAsync(packagePath, channels);
            return InstallResult.SuccessResult();
        }
    }
}
