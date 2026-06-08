using Apps2Samsung.Models;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Applies app-specific edits to a downloaded <c>.wgt</c> package before it is
    /// signed and installed. Each supported app provides one implementation; the
    /// installer picks the first one whose <see cref="CanHandle"/> matches the package.
    /// </summary>
    public interface IPackagePatcher
    {
        /// <summary>True if this patcher should process the given package (matched by wgt filename).</summary>
        bool CanHandle(string packagePath);

        /// <summary>Applies the app's configuration to the package in place. A no-op returns success.</summary>
        Task<InstallResult> ApplyAsync(string packagePath);
    }
}
