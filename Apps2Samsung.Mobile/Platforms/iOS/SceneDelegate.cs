using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Foundation;
using UIKit;

namespace Apps2Samsung.Mobile;

// iOS starts an app built with the iOS 27 SDK only when it adopts the UIScene life cycle
// ("UIScene life cycle is required for apps built with this SDK"); without it the app is
// stopped right after launch. Named in Info.plist's UIApplicationSceneManifest.
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
	// Light / dark comes from the scene's own traits: at connect for the theme the app starts in,
	// and on every trait change for a switch while it runs.
	public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
	{
		base.WillConnect(scene, session, connectionOptions);
		if (scene is UIWindowScene windowScene)
			ReportTheme(windowScene.TraitCollection);
		// Started by an apps2samsung:// install link (#1066).
		foreach (var context in connectionOptions.UrlContexts ?? new NSSet<UIOpenUrlContext>())
			InstallLinkInbox.Deliver(context.Url?.AbsoluteString);
	}

	// An install link while the app runs (#1066).
	public override void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
	{
		base.OpenUrlContexts(scene, urlContexts);
		foreach (var context in urlContexts)
			InstallLinkInbox.Deliver(context.Url?.AbsoluteString);
	}

	public override void DidUpdateCoordinateSpace(UIWindowScene windowScene, IUICoordinateSpace previousCoordinateSpace,
		UIInterfaceOrientation previousInterfaceOrientation, UITraitCollection previousTraitCollection)
	{
		base.DidUpdateCoordinateSpace(windowScene, previousCoordinateSpace, previousInterfaceOrientation, previousTraitCollection);
		ReportTheme(windowScene.TraitCollection);
	}

	private static void ReportTheme(UITraitCollection traits) =>
		DesignTheme.SetPlatformTheme(traits.UserInterfaceStyle switch
		{
			UIUserInterfaceStyle.Dark => AppTheme.Dark,
			UIUserInterfaceStyle.Light => AppTheme.Light,
			_ => AppTheme.Unspecified,
		});
}
