using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Apps2Samsung.Design;

namespace Apps2Samsung.Styles
{
    /// <summary>
    /// Builds the desktop head's resource dictionary from <see cref="DesignTokens"/> (Core), so the
    /// values live once and this head can't drift from mobile. Every colour token becomes
    /// <c>{Name}Color</c> and <c>{Name}Brush</c> inside Avalonia's light and dark theme dictionaries,
    /// so <c>{DynamicResource AccentBrush}</c> follows the theme switch. Radii, sizes and the type ramp
    /// are theme-independent and sit in the base dictionary.
    /// </summary>
    public static class DesignTheme
    {
        public static ResourceDictionary Build()
        {
            var light = new ResourceDictionary();
            var dark = new ResourceDictionary();
            foreach (var (name, token) in DesignTokens.Colors.All)
            {
                Add(light, name, token.Light);
                Add(dark, name, token.Dark);
            }

            var resources = new ResourceDictionary();
            resources.ThemeDictionaries[ThemeVariant.Light] = light;
            resources.ThemeDictionaries[ThemeVariant.Dark] = dark;

            foreach (var (name, value) in DesignTokens.Radius.All)
                resources["Radius" + name] = new CornerRadius(value);
            foreach (var (name, value) in DesignTokens.Size.All)
                resources["Size" + name] = value;
            foreach (var (name, value) in DesignTokens.Type.All)
                resources["Font" + name] = value;

            return resources;
        }

        private static void Add(ResourceDictionary dictionary, string name, string hex)
        {
            var color = Color.Parse(hex);
            dictionary[name + "Color"] = color;
            dictionary[name + "Brush"] = new SolidColorBrush(color);
        }

        /// <summary>The brush for a colour token in the current theme, for code that paints
        /// without a view to bind in (converters, view models). Resolved on every call so a theme
        /// switch is picked up.</summary>
        public static IBrush Brush(string token)
        {
            var app = Application.Current;
            if (app is not null && app.TryFindResource(token + "Brush", app.ActualThemeVariant, out var value)
                && value is IBrush brush)
                return brush;
            // Before the dictionary is registered (unit tests, design time): the light value.
            return new SolidColorBrush(Color.Parse(DesignTokens.Colors.All[token].Light));
        }
    }
}
