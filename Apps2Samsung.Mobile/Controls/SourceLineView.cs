using Apps2Samsung.Catalog;

namespace Apps2Samsung.Mobile.Controls;

/// <summary>
/// The line under an app that says where it comes from (#1018, mockup CatalogA): nothing for the
/// official catalog, the shield and the repository's name in the accent colour for a signed
/// repository, the warning circle in amber for an unverified source, the lock in muted grey for a
/// repository that could not be checked. The text comes from Core's <see cref="CatalogSourceLabels"/>.
/// </summary>
public static class SourceLineView
{
	/// <summary>A shield with a tick.</summary>
	public const string ShieldIcon = "M12,22 C12,22 20,18 20,12 L20,5 L12,2 L4,5 L4,12 C4,18 12,22 12,22 Z M9,12 L11,14 L15,10";
	/// <summary>A closed padlock.</summary>
	public const string LockIcon = "M7,11 L17,11 A2,2 0 0 1 19,13 L19,19 A2,2 0 0 1 17,21 L7,21 A2,2 0 0 1 5,19 L5,13 A2,2 0 0 1 7,11 Z M8,11 L8,7 A4,4 0 0 1 16,7 L16,11";
	/// <summary>A circle with an exclamation mark.</summary>
	public const string WarnIcon = "M12,3 A9,9 0 1 1 11.99,3 Z M12,8 L12,12 M12,16 L12.01,16";
	/// <summary>A key (the publisher's key changed).</summary>
	public const string KeyIcon = "M7.5,10 A5.5,5.5 0 1 1 7.49,10 Z M21,2 L11.4,11.6 M15.5,7.5 L18.5,10.5 L22,7 L19,4";

	/// <summary>The colour token a line of this kind is drawn in.</summary>
	public static string ColorToken(SourceLineKind kind) => kind switch
	{
		SourceLineKind.Repository => "Accent",
		SourceLineKind.Unverified => "Warn",
		_ => "Muted",
	};

	/// <summary>The icon and the text in one row; null for an official app (no line at all).</summary>
	public static View? Build(SourceLine line, double fontSize = 13)
	{
		if (!line.IsVisible)
			return null;
		var token = ColorToken(line.Kind);
		var icon = Icon(line.Kind switch
		{
			SourceLineKind.Repository => ShieldIcon,
			SourceLineKind.Blocked => LockIcon,
			_ => WarnIcon,
		}, token, fontSize);
		var text = new Label
		{
			Text = line.Text, FontSize = fontSize, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center,
			TextColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(token),
		};
		var row = new HorizontalStackLayout { Spacing = 5, Children = { icon, text } };
		SemanticProperties.SetDescription(row, line.Text);
		SemanticProperties.SetHint(row, line.Tip);
		return row;
	}

	/// <summary>A stroked 24-unit icon, drawn at <paramref name="size"/> in the colour of <paramref name="token"/>.</summary>
	public static Microsoft.Maui.Controls.Shapes.Path Icon(string data, string token, double size) => new()
	{
		Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString(data)!,
		Stroke = new SolidColorBrush(Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(token)),
		StrokeThickness = 2.4, StrokeLineCap = Microsoft.Maui.Controls.Shapes.PenLineCap.Round, StrokeLineJoin = Microsoft.Maui.Controls.Shapes.PenLineJoin.Round,
		Aspect = Stretch.Uniform, WidthRequest = size, HeightRequest = size, VerticalOptions = LayoutOptions.Center,
	};
}
