using Apps2Samsung.Configuration;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Adapts the mobile head's settings (the static <see cref="MobileSettings"/> + MAUI
/// <see cref="Preferences"/>) to the shared <see cref="IAppConfig"/>, so Core services (cert
/// provisioning/reuse, install, patchers) read settings the same way on both heads. Fields the
/// mobile head didn't persist before (force-login, overwrite, custom icons) are added here.
/// </summary>
public sealed class MobileAppConfig : IAppConfig
{
    private const string KeyForceLogin = "force_samsung_login";
    private const string KeyTryOverwrite = "try_overwrite";
    private const string KeyCustomIcons = "custom_app_icons_json";

    // ---- Install behaviour ----
    public bool DeletePreviousInstall { get => MobileSettings.DeletePreviousInstall; set => MobileSettings.DeletePreviousInstall = value; }
    public bool OpenAfterInstall { get => MobileSettings.OpenAfterInstall; set => MobileSettings.OpenAfterInstall = value; }
    public bool KeepWgtFile { get => MobileSettings.KeepWgtFile; set => MobileSettings.KeepWgtFile = value; }
    public bool TryOverwrite { get => Preferences.Get(KeyTryOverwrite, true); set => Preferences.Set(KeyTryOverwrite, value); }

    // ---- Catalog ----
    public bool ShowAllJellyfinVersions { get => MobileSettings.ShowAllJellyfinVersions; set => MobileSettings.ShowAllJellyfinVersions = value; }
    public string GitHubToken => MobileSettings.GitHubToken;

    // ---- Signing / certificates ----
    public bool PartnerSigning { get => MobileSettings.PartnerSigning; set => MobileSettings.PartnerSigning = value; }
    // Runtime-only, per-install (mirrors the desktop's [JsonIgnore] RequiresPartnerSigning).
    public bool RequiresPartnerSigning { get; set; }
    public bool ForceSamsungLogin { get => Preferences.Get(KeyForceLogin, false); set => Preferences.Set(KeyForceLogin, value); }
    public string ManualDuids { get => MobileSettings.ManualDuids; set => MobileSettings.ManualDuids = value; }
    // Root under which the generated signing profiles ("Jelly2Sams - Public/Partner") live.
    public string CertificateStorePath => Path.Combine(FileSystem.AppDataDirectory, "Certificate");

    // ---- Package patching ----
    public string CustomAppIconsJson { get => Preferences.Get(KeyCustomIcons, string.Empty); set => Preferences.Set(KeyCustomIcons, value ?? string.Empty); }
    public string TvAppChannelsJson { get => MobileSettings.TvAppChannelsJson; set => MobileSettings.TvAppChannelsJson = value; }
}
