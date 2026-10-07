using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Applies app-specific edits to a downloaded <c>.wgt</c> package before it is
    /// signed and installed. Each supported app provides one implementation; the
    /// installer runs every one whose <see cref="CanHandle"/> matches the package.
    /// <para>
    /// The installer owns the <see cref="PackageWorkspace"/>: it unpacks the package once, hands the
    /// same workspace to each matching patcher in turn, and rezips once at the end. Patchers edit
    /// files under <see cref="PackageWorkspace.Root"/> and never extract or repack themselves, so
    /// three matching patchers cost one unpack/rezip instead of three.
    /// </para>
    /// </summary>
    public interface IPackagePatcher
    {
        /// <summary>True if this patcher should process the given package (matched by wgt filename).</summary>
        bool CanHandle(string packagePath);

        /// <summary>
        /// Applies the app's configuration to the extracted package. A no-op returns success and
        /// leaves the workspace untouched, which the installer notices and skips the repack for.
        /// </summary>
        Task<InstallResult> ApplyAsync(PackageWorkspace ws);
    }
}
