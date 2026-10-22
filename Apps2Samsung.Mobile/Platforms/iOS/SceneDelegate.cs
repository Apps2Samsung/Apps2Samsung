using Foundation;

namespace Apps2Samsung.Mobile;

// iOS starts an app built with the iOS 27 SDK only when it adopts the UIScene life cycle
// ("UIScene life cycle is required for apps built with this SDK"); without it the app is
// stopped right after launch. Named in Info.plist's UIApplicationSceneManifest.
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
