using Apps2Samsung.Mobile.Resources.Styles;
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
