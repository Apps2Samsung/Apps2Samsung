using Apps2Samsung.Configuration;
using Apps2Samsung.Interfaces;
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

		builder.Services.AddSingleton<INetworkService>(_ => new NetworkService(customIpProvider: NetworkInfo.GetLocalIPv4));

		// The SDB engine needs a writable directory to persist its RSA auth keypair (the desktop
		// uses the profile dir; on Android there's no ambient writable path, so point it at the
		// app's private data dir before any connection). Set once, process-wide.
		SdbTcpDevice.KeyDirectory = FileSystem.AppDataDirectory;
		builder.Services.AddSingleton<ISdbEngine, InProcessSdbEngine>();

		// Samsung account OAuth (WebView + in-app loopback listener) — provides the token bundle
		// the certificate provisioning needs.
		builder.Services.AddSingleton<ISamsungLoginService, SamsungLoginService>();

		// Certificate provisioning: a plain HttpClient for the Samsung REST calls, the shared cert
		// service against Samsung's production endpoints, and the mobile provisioner that ties in
		// the DUID lookup + bundled CA files.
		builder.Services.AddSingleton(new HttpClient());
		builder.Services.AddSingleton<ITizenCertificateService>(sp =>
			new TizenCertificateService(sp.GetRequiredService<HttpClient>(), CertificateEndpoints.Default));
		builder.Services.AddSingleton<CertificateProvisioner>();

		// Install: download a .wgt and push it to the TV (resign -> [permit] -> install).
		builder.Services.AddSingleton<WgtInstaller>();

		// App catalog (App/Version dropdowns) — reuses the shared HttpClient for GitHub calls.
		builder.Services.AddSingleton<CatalogService>();

		// Self-update check (shared Core logic) — matches the .apk asset on the latest release.
		builder.Services.AddSingleton(sp => new GitHubUpdateChecker(sp.GetRequiredService<HttpClient>()));

		// Session (holds the Samsung sign-in for the app's lifetime) + the installer page.
		builder.Services.AddSingleton<SessionState>();
		builder.Services.AddSingleton<InstallerPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
