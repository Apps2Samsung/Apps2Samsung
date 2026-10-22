using Apps2Samsung.Certificate;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Shown when the Samsung account has no email address, which the distributor certificate needs
/// (issue #606). Only the user can fix it, on their account page, so the sheet says how and opens it.
/// </summary>
public partial class AccountEmailSheetPage : ContentPage
{
	public AccountEmailSheetPage()
	{
		InitializeComponent();

		var keys = new[] { "certNoEmailStep1", "certNoEmailStep2", "certNoEmailStep3" };
		for (var i = 0; i < keys.Length; i++)
		{
			if (i > 0)
				Steps.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(56, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") });

			var badge = new Border
			{
				WidthRequest = 28, HeightRequest = 28, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
				BackgroundColor = DesignTheme.ColorOf("AccentSoft"),
				Content = new Label
				{
					Text = (i + 1).ToString(), FontSize = 13, FontAttributes = FontAttributes.Bold,
					TextColor = DesignTheme.ColorOf("Accent"),
					HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
				},
			};
			var row = new Grid
			{
				ColumnDefinitions = { new ColumnDefinition(28), new ColumnDefinition(GridLength.Star) },
				ColumnSpacing = 12, Padding = new Thickness(16, 12),
			};
			row.Add(badge, 0, 0);
			row.Add(new Label { Text = L10n.Get(keys[i]), FontSize = 15, VerticalOptions = LayoutOptions.Center }, 1, 0);
			Steps.Add(row);
		}
	}

	private async void OnOpenAccountClicked(object? sender, EventArgs e)
	{
		try { await Launcher.Default.OpenAsync(SamsungAccountEmailMissingException.AccountUrl); }
		catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[certificate] Couldn't open the Samsung account page: {ex.Message}"); }
		await Navigation.PopModalAsync();
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();
}
