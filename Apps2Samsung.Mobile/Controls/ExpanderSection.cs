namespace Apps2Samsung.Mobile.Controls;

/// <summary>
/// A lightweight collapsible section: a bold tappable header (title + chevron) with a body that
/// shows/hides on tap. Keeps the long Settings page short by letting each block collapse — no
/// CommunityToolkit dependency, just a header + a hosted content view whose visibility we flip.
/// The nested child is the section body (this is the control's ContentProperty).
/// </summary>
[ContentProperty(nameof(SectionContent))]
public sealed class ExpanderSection : ContentView
{
	private readonly Label _title;
	private readonly Label _chevron;
	private readonly ContentView _host;

	public static readonly BindableProperty TitleProperty = BindableProperty.Create(
		nameof(Title), typeof(string), typeof(ExpanderSection), string.Empty,
		propertyChanged: (b, _, n) => ((ExpanderSection)b)._title.Text = (string)n);

	public static readonly BindableProperty IsExpandedProperty = BindableProperty.Create(
		nameof(IsExpanded), typeof(bool), typeof(ExpanderSection), false,
		propertyChanged: OnIsExpandedChanged);

	public static readonly BindableProperty SectionContentProperty = BindableProperty.Create(
		nameof(SectionContent), typeof(View), typeof(ExpanderSection), null,
		propertyChanged: (b, _, n) => ((ExpanderSection)b)._host.Content = (View?)n);

	public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
	public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
	public View? SectionContent { get => (View?)GetValue(SectionContentProperty); set => SetValue(SectionContentProperty, value); }

	public ExpanderSection()
	{
		_title = new Label { FontAttributes = FontAttributes.Bold, FontSize = 14, VerticalOptions = LayoutOptions.Center };
		_chevron = new Label { Text = "⌄", FontSize = 18, Opacity = 0.55, VerticalOptions = LayoutOptions.Center };
		_host = new ContentView { IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };

		var header = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto),
			},
			Padding = new Thickness(0, 4),
		};
		header.Add(_title, 0, 0);
		header.Add(_chevron, 1, 0);

		var tap = new TapGestureRecognizer();
		tap.Tapped += (_, _) => IsExpanded = !IsExpanded;
		header.GestureRecognizers.Add(tap);

		Content = new VerticalStackLayout { Spacing = 0, Children = { header, _host } };
	}

	private static void OnIsExpandedChanged(BindableObject b, object oldValue, object newValue)
	{
		var section = (ExpanderSection)b;
		var expanded = (bool)newValue;
		section._host.IsVisible = expanded;
		section._chevron.Text = expanded ? "⌃" : "⌄"; // ⌃ / ⌄
	}
}
