using System.Threading.Tasks;
using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.Core; // PackageWorkspace
using Apps2Samsung.Interfaces;    // IPackagePatcher
using Apps2Samsung.Models;        // InstallResult

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Applies the user's Camera PiP cameras to the package before install: rewrites the
    /// <c>window.PIP_CONFIG</c> object in the widget's <c>config.js</c> from
    /// <see cref="IAppConfig.CameraPipConfigJson"/>. Shared by both heads (each fills the JSON from
    /// its own settings form); the wgt editing itself lives in <see cref="CameraPipConfigInjector"/>.
    /// </summary>
    public sealed class CameraPipPackagePatcher : IPackagePatcher
    {
        private readonly IAppConfig _config;

        public CameraPipPackagePatcher(IAppConfig config) => _config = config;

        public bool CanHandle(string packagePath) => CameraPipConfigInjector.AppliesTo(packagePath);

        public async Task<InstallResult> ApplyAsync(PackageWorkspace ws)
        {
            var settings = CameraPipConfigInjector.ParseSettingsJson(_config.CameraPipConfigJson);
            await CameraPipConfigInjector.InjectAsync(ws, settings);
            return InstallResult.SuccessResult();
        }
    }
}
