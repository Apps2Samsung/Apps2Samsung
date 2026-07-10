using Apps2Samsung.Interfaces;
using Apps2Samsung.Services;
using Microsoft.Extensions.Logging;

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

		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
