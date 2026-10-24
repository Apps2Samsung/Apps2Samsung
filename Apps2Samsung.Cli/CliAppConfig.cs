using Apps2Samsung.Configuration;

namespace Apps2Samsung.Cli;

/// <summary>
/// The shared settings contract, filled from command-line flags for one run. The CLI persists
/// nothing of its own: the package patchers that need a configured server or account (Jellyfin,
/// ImmiTV, Camera PiP, TVApp channels, custom icons) stay off here, and a community package's
/// <c>user_settings</c> come in through <c>--set key=value</c>.
/// </summary>
public sealed class CliAppConfig : IAppConfig
{
    public bool DeletePreviousInstall { get; set; }
    public bool OpenAfterInstall { get; set; } = true;
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
    public string LocalIp => string.Empty;
}
