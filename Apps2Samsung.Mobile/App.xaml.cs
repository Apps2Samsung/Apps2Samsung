using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Resolve the first page from the DI container so its Core services are injected.
		var services = IPlatformApplication.Current!.Services;
		return new Window(new NavigationPage(services.GetRequiredService<MainPage>()));
	}
}