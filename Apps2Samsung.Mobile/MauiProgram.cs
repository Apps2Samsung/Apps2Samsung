using System.Net;
using System.Net.Sockets;
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
		// On Android, NetworkInterface doesn't reliably classify the Wi-Fi adapter as
		// Ethernet/Wireless80211 and IPv4Mask is unavailable, so the Core scan's interface
		// enumeration yields nothing. Feed the device's own IPv4 as the custom scan IP: the
		// scanner then covers its /24 (the mask fallback), which finds TVs on the same LAN.
		builder.Services.AddSingleton<INetworkService>(_ => new NetworkService(customIpProvider: GetDeviceIPv4));

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

	// The device's own LAN IPv4, via the route the OS picks for an outbound socket (no packets are
	// sent — Connect on a UDP socket just resolves the local endpoint). Works on Android where
	// interface enumeration/masks don't; null if offline.
	private static string? GetDeviceIPv4()
	{
		try
		{
			using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
			socket.Connect("8.8.8.8", 65530);
			return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
		}
		catch
		{
			return null;
		}
	}
}
