namespace Apps2Samsung.Update
{
    /// <summary>
    /// Which release asset the desktop app updates itself from. Every release also ships the CLI
    /// for the same platforms (<c>Apps2Samsung.Cli-v3.3.0-win-x64.zip</c>, before 3.3.0
    /// <c>Apps2Samsung-cli-…</c>), and GitHub lists assets by name, so a matcher on platform and
    /// extension alone picked the CLI archive first: the update downloaded it and then failed with
    /// "Could not find application files in the update package". Only the app's own archive counts.
    /// </summary>
    public static class UpdateAssets
    {
        /// <summary>
        /// True for the desktop app's archive for <paramref name="platformSuffix"/> ("win-x64",
        /// "linux-arm64", "macos-x64"): <c>Apps2Samsung-v&lt;version&gt;-&lt;suffix&gt;.zip</c> or <c>.tar.gz</c>.
        /// </summary>
        public static bool IsDesktopArchive(string? name, string platformSuffix) =>
            IsArchive(name) && !IsCli(name!)
            && name!.EndsWith("-" + platformSuffix + Extension(name), StringComparison.OrdinalIgnoreCase);

        /// <summary>Any desktop archive, for when none matches the platform (the old fallback, without the CLI).</summary>
        public static bool IsAnyDesktopArchive(string? name) => IsArchive(name) && !IsCli(name!);

        /// <summary>A CLI archive, under either naming.</summary>
        public static bool IsCli(string name) =>
            name.StartsWith("Apps2Samsung.Cli-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Apps2Samsung-cli-", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-cli-", StringComparison.OrdinalIgnoreCase);

        private static bool IsArchive(string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));

        private static string Extension(string name) =>
            name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : ".zip";
    }
}
