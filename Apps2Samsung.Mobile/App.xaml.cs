using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// The design tokens (Core) as resources, before any page is built.
		Apps2Samsung.Mobile.Resources.Styles.DesignTheme.Apply(this);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// The Shell resolves each tab's page from the DI container (ContentTemplate), so the Core
		// services reach them the same way they did through NavigationPage.
		return new Window(new AppShell());
	}
}