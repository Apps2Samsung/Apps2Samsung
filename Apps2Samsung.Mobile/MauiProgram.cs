using Apps2Samsung.Certificate;
using Apps2Samsung.Configuration;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Packaging;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Pages;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Update;
using Microsoft.Extensions.Logging;
using TizenSdb.SdbClient;

namespace Apps2Samsung.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// Persist Trace diagnostics to a file so the mobile app is actually debuggable (shared infra
		// with the desktop head). Previously mobile only logged to the transient Android console.
		Apps2Samsung.Diagnostics.FileLog.Initialize(System.IO.Path.Combine(FileSystem.AppDataDirectory, "Logs"));

		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// --------------------
		// Shared Core services
		// --------------------
		// The TV scan is fully portable. This head has no arp vendor lookup and no SDB-backed
		// name resolver yet, so NetworkService runs with those left null — detection relies only
		// on the open debug/REST ports, so found TVs still come back (just without a friendly name).
		// On Android, NetworkInterface doesn't reliably classify the Wi-Fi adapter as
		// Ethernet/Wireless80211 and IPv4Mask is unavailable, so the Core scan's interface
		// enumeration yields nothing. Feed the device's own IPv4 as the custom scan IP: the
		// scanner then covers its /24 (the mask fallback), which finds TVs on the same LAN.
		// Shared settings contract — the mobile head's settings adapted to Core's IAppConfig, so the
		// shared cert/install/patcher services read settings uniformly across both heads.
		builder.Services.AddSingleton<IAppConfig, MobileAppConfig>();

		// 64 hosts at a time (128 sockets): a /24 in four rounds of at most a second each, and well
		// under the open-file limit an iOS process starts with, where the desktop's all-at-once sweep
		// of 500 sockets would fail with "too many open files" (#824).
		builder.Services.AddSingleton<INetworkService>(_ => new NetworkService(
			customIpProvider: NetworkInfo.GetLocalIPv4, maxConcurrentHosts: 64));

		// The SDB engine needs a writable directory to persist its RSA auth keypair (the desktop
		// uses the profile dir; Android and iOS have no ambient writable path, so point it at the
		// app's private data dir before any connection). Set once, process-wide.
		SdbTcpDevice.KeyDirectory = FileSystem.AppDataDirectory;
		builder.Services.AddSingleton<ISdbEngine, Apps2Samsung.Sdb.InProcessSdbEngine>();

		// Samsung account OAuth (WebView + in-app loopback listener) — provides the token bundle
		// the certificate provisioning needs.
		builder.Services.AddSingleton<ISamsungLoginService, SamsungLoginService>();

		// Certificate provisioning: a plain HttpClient for the Samsung REST calls, the shared cert
		// service against Samsung's production endpoints, and the mobile provisioner that ties in
		// the DUID lookup + bundled CA files.
		builder.Services.AddSingleton(new HttpClient());
		builder.Services.AddSingleton<ITizenCertificateService>(sp =>
			new TizenCertificateService(sp.GetRequiredService<HttpClient>(), CertificateEndpoints.Default));
		// Shared reuse-aware provisioning: reuses a valid profile covering the TV and only logs in
		// when it must (re)generate. Depends on the shared cert service + this head's login service.
		builder.Services.AddSingleton<CertificateProvisioningService>();
		builder.Services.AddSingleton<CertificateProvisioner>();
		builder.Services.AddSingleton<CertificateFlow>();

		// Package patchers applied at install (shared Core IPackagePatchers). Mobile ships no bundled
		// "oblong" tiles, so a no-op oblong source — only user-supplied custom PNG icons apply here.
		builder.Services.AddSingleton<IOblongIconSource, NoOblongIconSource>();
		builder.Services.AddSingleton<IPackagePatcher, CustomIconPackagePatcher>();
		builder.Services.AddSingleton<IPackagePatcher, AppTitlePackagePatcher>();
		// ImmiTV: Immich server + login → js/config.js IMMICH_DEFAULTS, configured in Settings.
		// No-ops when nothing is set.
		builder.Services.AddSingleton<IPackagePatcher, ImmiTvPackagePatcher>();
		// Opt-in TizenTube Cobalt proxy-host rewrite (Settings → TizenTube); no-op unless enabled.
		builder.Services.AddSingleton<IPackagePatcher, TizenTubeProxyPatcher>();
		// Camera PiP: cameras → config.js window.PIP_CONFIG at install (shared Core patcher).
		builder.Services.AddSingleton<IPackagePatcher, CameraPipPackagePatcher>();
		// A community package's user_settings (API keys, server addresses asked for before the
		// install) → the file the catalog names, by exact-text replacement (#791).
		builder.Services.AddSingleton<IPackagePatcher, UserSettingsPatcher>();

		// The pre-resolved catalog from apps2samsung.com (one download per 10 minutes, shared), so
		// the app list and the info view load without api.github.com calls. Cached in the app's
		// private data dir next to the manifest cache.
		builder.Services.AddSingleton(sp => new Apps2Samsung.Catalog.ResolvedCatalogService(
			sp.GetRequiredService<HttpClient>(),
			Path.Combine(FileSystem.AppDataDirectory, Apps2Samsung.Catalog.ResolvedCatalogService.CacheFileName)));

		// Shared app catalog for the icon/title editor (same list as desktop).
		builder.Services.AddSingleton<Apps2Samsung.Catalog.AppCatalog>(sp => new Apps2Samsung.Catalog.AppCatalog(
			new Apps2Samsung.Helpers.Core.AddLatestRelease(
				sp.GetRequiredService<HttpClient>(), () => MobileSettings.GitHubToken,
				sp.GetRequiredService<Apps2Samsung.Catalog.ResolvedCatalogService>()),
			sp.GetRequiredService<IOblongIconSource>()));
		// Jellyfin config injection (server URL + auto-login + custom CSS), configured via the
		// Settings → Jellyfin page. No-ops when no server is set (empty JellyfinFullUrl).
		builder.Services.AddSingleton<IPackagePatcher>(sp =>
			new Apps2Samsung.Helpers.Jellyfin.JellyfinPackagePatcher(
				sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IAppConfig>()));

		// Install: download a .wgt and push it to the TV (patch -> resign -> [permit] -> install).
		builder.Services.AddSingleton<WgtInstaller>();

		// App catalog (App/Version dropdowns) — reuses the shared HttpClient for GitHub calls.
		builder.Services.AddSingleton<CatalogService>();

		// Self-update check (shared Core logic) — matches the .apk asset on the latest release.
		builder.Services.AddSingleton(sp => new GitHubUpdateChecker(sp.GetRequiredService<HttpClient>()));
		// The in-app updater: the check, the sheet, the download, the hand-off to the installer.
		builder.Services.AddSingleton<UpdateFlow>();

		// Session (holds the Samsung sign-in for the app's lifetime) + the installer page.
		builder.Services.AddSingleton<SessionState>();
		// Transient, not a singleton: {l:Localize} resolves while a page is being built, so a cached
		// installer page would serve the language it was born with for the whole life of the process -
		// and Android keeps the process alive across closing and reopening the app, so "restart it"
		// didn't rebuild it either. A language change resolves a fresh one (see SettingsLanguagePage).
		builder.Services.AddTransient<InstallerPage>();
		// The TVs and the one in use, shared by every tab (the ▾ in each top bar), and the other tab roots.
		builder.Services.AddSingleton<TvSelection>();
		builder.Services.AddTransient<InstalledAppsPage>();
		builder.Services.AddTransient<RemotePage>();
		builder.Services.AddTransient<SettingsPage>();
		// The icon/title editor needs the catalog services injected (shared app list).
		builder.Services.AddTransient<AppIconsPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
