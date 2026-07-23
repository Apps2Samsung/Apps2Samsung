namespace Apps2Samsung.Configuration
{
    /// <summary>
    /// The slice of user settings that the shared Core services (certificate provisioning + reuse,
    /// install orchestration, package patchers) need to read. Each head implements it over its own
    /// store — the desktop's <c>settings.json</c> god-object, or the mobile head's MAUI Preferences —
    /// so the moved logic no longer depends on a particular app's settings type.
    ///
    /// This is deliberately the *shared subset*, not everything each head persists (the desktop keeps
    /// far more — Jellyfin server creds, theme, etc.). Grows only as later stages move more logic into
    /// Core.
    /// </summary>
    public interface IAppConfig
    {
        // ---- Install behaviour ----
        /// <summary>Uninstall the previous version before installing.</summary>
        bool DeletePreviousInstall { get; set; }
        /// <summary>Launch the app on the TV after a successful install.</summary>
        bool OpenAfterInstall { get; set; }
        /// <summary>Keep the downloaded/patched .wgt instead of deleting it.</summary>
        bool KeepWgtFile { get; set; }
        /// <summary>Allow an overwrite-install (retry after removing the old copy). Transient control flag.</summary>
        bool TryOverwrite { get; set; }

        // ---- Catalog ----
        /// <summary>List every Jellyfin release rather than only the latest.</summary>
        bool ShowAllJellyfinVersions { get; set; }
        /// <summary>GitHub PAT used to avoid API rate limits (empty if unset). Read-only on the request path.</summary>
        string GitHubToken { get; }

        // ---- Signing / certificates ----
        /// <summary>Opt-in Partner-level distributor signing (default Public).</summary>
        bool PartnerSigning { get; set; }
        /// <summary>Per-install bump to Partner because the selected package declares a restricted
        /// privilege. Runtime-only (not persisted).</summary>
        bool RequiresPartnerSigning { get; set; }
        /// <summary>Force a fresh Samsung login + profile even when a reusable cert exists.</summary>
        bool ForceSamsungLogin { get; set; }
        /// <summary>Extra TV DUIDs to pre-authorize in the distributor cert (newline/comma separated).</summary>
        string ManualDuids { get; set; }
        /// <summary>Directory that holds the generated signing profiles (e.g. "Jelly2Sams - Public/Partner").</summary>
        string CertificateStorePath { get; }

        // ---- Package patching ----
        /// <summary>JSON map { appKey -> "oblong" | custom launcher PNG path } applied to a wgt at install.</summary>
        string CustomAppIconsJson { get; set; }
        /// <summary>JSON array of { name, url } TVApp channels injected into a TVApp wgt at install.</summary>
        string TvAppChannelsJson { get; set; }

        // ---- Jellyfin package patching ----
        // The shared JellyfinPackagePatcher reads these to inject the server address, auto-login
        // credentials, custom CSS, and optional script/plugin patches into a Jellyfin .wgt. All are
        // read-only on the install path: an empty JellyfinFullUrl means "no server configured →
        // nothing to inject". Each head fills them from its own store (desktop settings.json / mobile
        // Preferences). See [[project_apps2samsung_rebrand]].
        /// <summary>Normalized full server URL (scheme://host:port[/base]); empty when unconfigured.</summary>
        string JellyfinFullUrl { get; }
        /// <summary>Server's self-reported LAN address, used as a reachable fallback in the server list.</summary>
        string JellyfinServerLocalAddress { get; }
        /// <summary>Access token from username/password auth; enables auto-login injection when set.</summary>
        string JellyfinAccessToken { get; }
        /// <summary>Authenticated user id; paired with the access token for auto-login.</summary>
        string JellyfinUserId { get; }
        /// <summary>Real server GUID from /System/Info/Public (prevents ServerMismatch on auto-login).</summary>
        string JellyfinServerId { get; }
        /// <summary>Human-readable server name shown in the Jellyfin server picker.</summary>
        string JellyfinServerName { get; }
        /// <summary>Rewrite index.html to run bundled server scripts / plugin patches.</summary>
        bool UseServerScripts { get; }
        /// <summary>Apply the YouTube-plugin ("error 153") fix to the package.</summary>
        bool PatchYoutubePlugin { get; }
        /// <summary>Inject the dev-logs websocket shim (streams client logs to LocalIp:54321).</summary>
        bool EnableDevLogs { get; }
        /// <summary>User custom CSS injected into index.html (empty = none).</summary>
        string CustomCss { get; }
        /// <summary>Comma-separated plugin ids to skip when applying server-script plugin patches.</summary>
        string DisabledPluginIds { get; }
        /// <summary>This machine's LAN IP, the dev-logs websocket endpoint host.</summary>
        string LocalIp { get; }
    }
}
