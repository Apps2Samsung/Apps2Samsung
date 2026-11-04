using System;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// What the toolbox's Package file section borrows from the main window (#979): the file
    /// pickers, which need the window's storage provider, and the install's customization step,
    /// which lives in the installer service.
    /// </summary>
    /// <param name="PickPackage">A .wgt/.tpk from disk, or null when nothing was picked.</param>
    /// <param name="PickSavePath">Where the signed copy goes, offered under the suggested name; null when cancelled.</param>
    /// <param name="Customize">The per-app modifications an install makes before signing, on the file in place (TV IP, package path).</param>
    public sealed record TvToolboxPackageHooks(
        Func<Task<string?>> PickPackage,
        Func<string, Task<string?>> PickSavePath,
        Func<string, string, Task> Customize);
}
