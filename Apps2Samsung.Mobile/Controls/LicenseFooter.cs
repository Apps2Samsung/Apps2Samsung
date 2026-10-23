using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Mobile.Localization;

namespace Apps2Samsung.Mobile.Controls;

/// <summary>
/// The licence line closing the Install tab and Settings (#820): "MIT License" opens the licence,
/// the name opens the website.
/// </summary>
public sealed class LicenseFooter : Label
{
	public LicenseFooter()
	{
		HorizontalOptions = LayoutOptions.Center;
		FontSize = 11;
		Opacity = 0.55;
		FormattedText = new FormattedString
		{
			Spans =
			{
				Link(L10n.Get("lblMitLicense"), Constants.Links.License),
				new Span { Text = " · " },
				Link(Constants.Links.AuthorName, Constants.Links.Website),
			},
		};
	}

	private static Span Link(string text, string url)
	{
		var span = new Span { Text = text, TextDecorations = TextDecorations.Underline };
		span.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await LinkOpener.OpenAsync(url)) });
		return span;
	}
}

/// <summary>Opens a page in the browser; a phone without one (or a cancelled chooser) just stays put.</summary>
public static class LinkOpener
{
	public static async Task OpenAsync(string url)
	{
		try { await Launcher.OpenAsync(url); }
		catch { /* no browser / cancelled */ }
	}
}
