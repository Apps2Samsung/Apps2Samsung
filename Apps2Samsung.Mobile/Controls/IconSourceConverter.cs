using System.Globalization;

namespace Apps2Samsung.Mobile.Controls;

/// <summary>
/// An app row's icon from its <c>IconUrl</c>: a web address (the store icons of the TV-app list) is
/// loaded as a URI, anything else is a file on the phone (a catalog icon cached by
/// <c>CatalogIcons</c>, #1000). A bare path string would be read as a file:// URI and show nothing.
/// </summary>
public sealed class IconSourceConverter : IValueConverter
{
	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not string text || string.IsNullOrWhiteSpace(text))
			return null;
		return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
			? ImageSource.FromUri(new Uri(text))
			: ImageSource.FromFile(text);
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		throw new NotSupportedException();
}
