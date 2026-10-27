using Apps2Samsung.Configuration;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Cli;

/// <summary>
/// The shared settings contract, filled from command-line flags for one run. The CLI persists
/// nothing of its own: the package patchers that need a configured server or account (Jellyfin,
/// ImmiTV, Camera PiP, TVApp channels, custom icons) stay off here, and a community package's
/// <c>user_settings</c> come in through <c>--set key=value</c>. The package-id renames live in the
/// desktop's settings.json (<see cref="DesktopSettings"/>), shared with the desktop app.
/// </summary>
public sealed class CliAppConfig : IAppConfig
{
    private readonly DesktopSettings _desktop;

    public CliAppConfig(DesktopSettings desktop)
    {
        _desktop = desktop;
    }

    public bool DeletePreviousInstall { get; set; }
    // The install command launches the app itself, so it can report whether that worked.
    public bool OpenAfterInstall { get; set; }
    public bool KeepWgtFile { get; set; }
    public bool TryOverwrite { get; set; } = true;

    public bool ShowAllJellyfinVersions { get; set; }
    public string GitHubToken { get; set; } = string.Empty;

    public bool PartnerSigning { get; set; }
    public bool RequiresPartnerSigning { get; set; }
    public bool ForceSamsungLogin { get; set; }
    public string ManualDuids { get; set; } = string.Empty;
    public string CertificateStorePath => CliPaths.CertificateStore;

    public string CustomAppIconsJson { get; set; } = string.Empty;
    public string CustomAppTitlesJson { get; set; } = string.Empty;
    public string TvAppChannelsJson { get; set; } = string.Empty;
    public string AppUserSettingsJson { get; set; } = string.Empty;

    // Which packages were installed under another id on which TV (#839). Without it an update would
    // push the original (blocked) id again. Kept in the desktop's settings.json, so the desktop and the
    // CLI recognise each other's renames; a renames file an older CLI kept on its own is folded in once.
    private bool _legacyMerged;
    public string PackageIdRenamesJson
    {
        get
        {
            if (!_legacyMerged)
            {
                _legacyMerged = true;
                MergeLegacyRenames();
            }
            return _desktop.PackageIdRenamesJson;
        }
        set => _desktop.PackageIdRenamesJson = value ?? string.Empty;
    }

    private void MergeLegacyRenames()
    {
        var legacyPath = Path.Combine(CliPaths.CliDir, "package-id-renames.json");
        if (!File.Exists(legacyPath))
            return;
        try
        {
            var json = _desktop.PackageIdRenamesJson;
            foreach (var record in PackageIdRenameStore.Read(File.ReadAllText(legacyPath)))
            {
                // The desktop's own record for the same TV and package wins.
                if (PackageIdRenameStore.Find(json, record.TvDuid, record.OriginalPackageId) is null)
                    json = PackageIdRenameStore.Set(json, record);
            }
            _desktop.PackageIdRenamesJson = json;
            File.Delete(legacyPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Left in place; the next run tries again.
        }
    }

    public string ImmiTvServerUrl => string.Empty;
    public string ImmiTvEmail => string.Empty;
    public string ImmiTvPassword => string.Empty;
    public string ImmiTvApiKey => string.Empty;
    public bool TizenTubeProxyOverride => false;
    public string TizenTubeProxyHost => string.Empty;
    public string CameraPipConfigJson => string.Empty;

    public string JellyfinFullUrl => string.Empty;
    public string JellyfinServerLocalAddress => string.Empty;
    public string JellyfinAccessToken => string.Empty;
    public string JellyfinUserId => string.Empty;
    public string JellyfinServerId => string.Empty;
    public string JellyfinServerName => string.Empty;
    public bool UseServerScripts => false;
    public bool PatchYoutubePlugin => false;
    public bool EnableDevLogs => false;
    public string CustomCss => string.Empty;
    public string DisabledPluginIds => string.Empty;
    public string LocalIp { get; set; } = string.Empty;
}
