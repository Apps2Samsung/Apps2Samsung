using System.Threading.Tasks;
using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.Core; // PackageWorkspace
using Apps2Samsung.Interfaces;    // IPackagePatcher
using Apps2Samsung.Models;        // InstallResult

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Applies the user's ImmiTV configuration to the package before install: rewrites the
    /// <c>IMMICH_DEFAULTS</c> object in <c>js/config.js</c> with the server / login details from
    /// <see cref="IAppConfig"/>. Shared by both heads (each fills the config from its own store);
    /// the wgt editing itself lives in <see cref="ImmiTvConfigInjector"/>.
    /// </summary>
    public sealed class ImmiTvPackagePatcher : IPackagePatcher
    {
        private readonly IAppConfig _config;

        public ImmiTvPackagePatcher(IAppConfig config) => _config = config;

        public bool CanHandle(string packagePath) => ImmiTvConfigInjector.AppliesTo(packagePath);

        public async Task<InstallResult> ApplyAsync(PackageWorkspace ws)
        {
            var defaults = new ImmiTvDefaults(
                _config.ImmiTvServerUrl ?? string.Empty,
                _config.ImmiTvEmail ?? string.Empty,
                _config.ImmiTvPassword ?? string.Empty,
                _config.ImmiTvApiKey ?? string.Empty);

            await ImmiTvConfigInjector.InjectAsync(ws, defaults);
            return InstallResult.SuccessResult();
        }
    }
}
