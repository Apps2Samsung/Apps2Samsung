using Apps2Samsung.Design;

namespace Apps2Samsung.Mobile.Resources.Styles;

/// <summary>
/// Fills the application's resources from <see cref="DesignTokens"/> (Core), so the values live once
/// and this head can't drift from desktop. Every colour token becomes <c>{Name}Color</c> and
/// <c>{Name}Brush</c> for the theme in effect, and is written again when the OS theme flips, so
/// <c>{DynamicResource AccentColor}</c> in a page follows light / dark without an AppThemeBinding.
/// Radii, sizes and the type ramp are registered once as doubles.
/// </summary>
public static class DesignTheme
{
    public static void Apply(Application app)
    {
        foreach (var (name, value) in DesignTokens.Radius.All)
            app.Resources["Radius" + name] = value;
        foreach (var (name, value) in DesignTokens.Size.All)
            app.Resources["Size" + name] = value;
        foreach (var (name, value) in DesignTokens.Type.All)
            app.Resources["Font" + name] = value;

        Fill(app);
        app.RequestedThemeChanged += (_, _) => Fill(app);
    }

    // The theme the platform reported itself, ahead of app.RequestedTheme. iOS sets it from the
    // window scene (see Platforms/iOS/SceneDelegate.cs): MAUI reads the theme there from
    // UIScreen.MainScreen, which under the UIScene life cycle does not follow dark mode.
    private static AppTheme? _platformTheme;

    /// <summary>Called by a head that knows the theme better than <see cref="Application.RequestedTheme"/>;
    /// writes the colours again when it changed.</summary>
    public static void SetPlatformTheme(AppTheme theme)
    {
        if (theme == AppTheme.Unspecified || theme == _platformTheme)
            return;
        _platformTheme = theme;
        if (Application.Current is { } app)
            Fill(app);
    }

    private static void Fill(Application app)
    {
        var dark = (_platformTheme ?? app.RequestedTheme) == AppTheme.Dark;
        foreach (var (name, token) in DesignTokens.Colors.All)
        {
            var color = Microsoft.Maui.Graphics.Color.FromArgb(dark ? token.Dark : token.Light);
            app.Resources[name + "Color"] = color;
            app.Resources[name + "Brush"] = new SolidColorBrush(color);
        }
    }

    /// <summary>The colour for a token in the theme in effect, for code that paints without a
    /// page to bind in. Resolved on every call so a theme switch is picked up.</summary>
    public static Color ColorOf(string token)
    {
        if (Application.Current?.Resources.TryGetValue(token + "Color", out var value) == true
            && value is Color color)
            return color;
        return Microsoft.Maui.Graphics.Color.FromArgb(DesignTokens.Colors.All[token].Light);
    }
}
