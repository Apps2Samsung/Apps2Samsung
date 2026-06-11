using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Litefin
{
    /// <summary>
    /// Applies the user's Litefin (MoazSalem/litefin) configuration to the package before install.
    /// Currently optional: swaps the launcher icon for a 16:9 "oblong" variant on older Tizen 5.5 TVs.
    /// </summary>
    public class LitefinPackagePatcher : IPackagePatcher
    {
        private static readonly Uri OblongIconUri = new("avares://Apps2Samsung/Assets/Litefin/oblong-icon.png");

        public bool CanHandle(string packagePath)
            => Path.GetFileName(packagePath).Contains("litefin", StringComparison.OrdinalIgnoreCase);

        public async Task<InstallResult> ApplyAsync(string packagePath)
        {
            if (!AppSettings.Default.LitefinUseOblongIcon)
            {
                Trace.WriteLine("[Litefin] Nothing configured; leaving package unchanged.");
                return InstallResult.SuccessResult();
            }

            using var ws = PackageWorkspace.Extract(packagePath);
            await WgtIconPatcher.SwapLauncherIconAsync(ws, OblongIconUri);
            ws.Repack();

            return InstallResult.SuccessResult();
        }
    }
}
