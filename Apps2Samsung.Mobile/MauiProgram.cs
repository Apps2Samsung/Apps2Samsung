using Apps2Samsung.Interfaces;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Services;
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
		builder.Services.AddSingleton<INetworkService>(_ => new NetworkService());

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

		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
