using System.Globalization;

namespace Apps2Samsung.Mobile.Localization;

/// <summary>
/// XAML markup extension for translated strings: <c>Text="{l:Localize lblSettings}"</c>, mirroring the
/// desktop head's <c>{l:Localize}</c> so a key reads the same in both heads' markup. Resolved once when
/// the page is built, which is why changing language asks for a restart.
/// </summary>
[ContentProperty(nameof(Key))]
public sealed class LocalizeExtension : IMarkupExtension<string>
{
    /// <summary>The en.json key to look up.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Optional format applied to the translated value, e.g. to append a unit.</summary>
    public string? Format { get; set; }

    public string ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
            return string.Empty;

        var value = L10n.Get(Key);
        return string.IsNullOrEmpty(Format)
            ? value
            : string.Format(CultureInfo.CurrentCulture, Format, value);
    }

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
