using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Catalog;
using Microsoft.Maui.Controls;

namespace Apps2Samsung.Mobile.Pages;

public partial class BuildInfoPage : ContentPage
{
	private readonly CatalogService _catalog;
	private bool _loaded;

	public BuildInfoPage(CatalogService catalog)
	{
		InitializeComponent();
		_catalog = catalog;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loaded)
			return;
		_loaded = true;

		try
		{
			var result = await _catalog.LoadBuildInfoAsync();
			var jellyfin = result.JellyfinBuilds.Select(Row.From).ToList();
			var community = result.CommunityApps.Select(Row.From).ToList();

			BindableLayout.SetItemsSource(JellyfinList, jellyfin);
			BindableLayout.SetItemsSource(CommunityList, community);
			JellyfinHeader.IsVisible = jellyfin.Count > 0;
			CommunityHeader.IsVisible = community.Count > 0;

			Busy.IsRunning = false;
			Busy.IsVisible = false;
			StatusLabel.IsVisible = jellyfin.Count == 0 && community.Count == 0;
			StatusLabel.Text = "Couldn't load the catalog (offline or GitHub rate-limited).";
		}
		catch (Exception ex)
		{
			Busy.IsRunning = false;
			Busy.IsVisible = false;
			StatusLabel.Text = $"Couldn't load the catalog: {ex.Message}";
		}
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	// View-row adapter over the Core BuildInfoItem (turns the preview URL into an ImageSource).
	private sealed class Row
	{
		public string Name { get; init; } = string.Empty;
		public string Version { get; init; } = string.Empty;
		public string Description { get; init; } = string.Empty;
		public bool HasVersion => !string.IsNullOrWhiteSpace(Version);
		public ImageSource? Preview { get; init; }

		public static Row From(BuildInfoItem item) => new()
		{
			Name = item.Name,
			Version = item.Version,
			Description = item.Description,
			Preview = string.IsNullOrWhiteSpace(item.PreviewImageUrl)
				? null
				: ImageSource.FromUri(new Uri(item.PreviewImageUrl!)),
		};
	}
}
