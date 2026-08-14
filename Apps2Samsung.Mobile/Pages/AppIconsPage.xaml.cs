using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Settings → App icons. Presents the same manifest-driven app list as the desktop head (built by the
/// shared Core <see cref="AppCatalog"/>) and lets the user give each app a custom launcher icon (PNG)
/// and/or a custom title. Choices are persisted per app in <see cref="MobileSettings.CustomAppIconsJson"/>
/// / <see cref="MobileSettings.CustomAppTitlesJson"/> and applied at install by the shared
/// <c>CustomIconPackagePatcher</c> / <c>AppTitlePackagePatcher</c> (#521).
/// </summary>
public partial class AppIconsPage : ContentPage
{
	private readonly CatalogService _catalog;
	private readonly AppCatalog _appCatalog;
	private readonly List<AppRow> _rows = new();
	private bool _loaded;

	private sealed record AppRow(string Key, Label IconSummary)
	{
		public string? IconPath { get; set; }
	}

	public AppIconsPage(CatalogService catalog, AppCatalog appCatalog)
	{
		InitializeComponent();
		_catalog = catalog;
		_appCatalog = appCatalog;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loaded)
			return;
		_loaded = true;
		await LoadAsync();
	}

	private async Task LoadAsync()
	{
		try
		{
			var manifest = await _catalog.GetManifestAsync();
			var entries = await _appCatalog.BuildAsync(manifest);

			var iconMap = LoadMap(MobileSettings.CustomAppIconsJson);
			var titleMap = LoadMap(MobileSettings.CustomAppTitlesJson);

			IconsContainer.Children.Clear();
			_rows.Clear();
			foreach (var entry in entries)
			{
				iconMap.TryGetValue(entry.Key, out var icon);
				titleMap.TryGetValue(entry.Key, out var title);
				AddRow(entry, icon, title);
			}

			StatusLabel.Text = _rows.Count == 0
				? "No apps loaded (offline or GitHub rate-limited). Add a token in Settings and reopen."
				: string.Empty;
			StatusLabel.IsVisible = _rows.Count == 0;
		}
		catch (Exception ex)
		{
			StatusLabel.Text = $"Couldn't load the app list: {ex.Message}";
			StatusLabel.IsVisible = true;
		}
	}

	private void AddRow(AppCatalogEntry entry, string? iconValue, string? titleValue)
	{
		var nameLabel = new Label
		{
			Text = entry.DisplayName,
			FontAttributes = FontAttributes.Bold,
			VerticalOptions = LayoutOptions.Center,
		};

		var titleEntry = new Entry
		{
			Text = titleValue ?? string.Empty,
			Placeholder = entry.DisplayName,
			BackgroundColor = Colors.Transparent,
		};

		var iconSummary = new Label
		{
			FontSize = 12,
			Opacity = 0.6,
			LineBreakMode = LineBreakMode.TailTruncation,
			VerticalOptions = LayoutOptions.Center,
		};
		var chooseBtn = new Button { Text = "Icon…", FontSize = 13, Padding = new Thickness(10, 0), HeightRequest = 40, CornerRadius = 6 };
		var clearBtn = new Button
		{
			Text = "✕",
			FontSize = 14,
			Padding = 0,
			WidthRequest = 40,
			HeightRequest = 40,
			CornerRadius = 6,
			BackgroundColor = Colors.Transparent,
			BorderWidth = 1,
			BorderColor = Color.FromArgb("#CDD3D8"),
			TextColor = Color.FromArgb("#B00020"),
		};

		var grid = new Grid
		{
			ColumnSpacing = 8,
			RowSpacing = 2,
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto),
				new ColumnDefinition(new GridLength(40)),
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
			},
		};
		// Row 0: app name. Row 1: custom-title entry (spans). Row 2: icon summary + choose/clear.
		Grid.SetColumn(nameLabel, 0); Grid.SetRow(nameLabel, 0); Grid.SetColumnSpan(nameLabel, 3);
		Grid.SetColumn(titleEntry, 0); Grid.SetRow(titleEntry, 1); Grid.SetColumnSpan(titleEntry, 3);
		Grid.SetColumn(iconSummary, 0); Grid.SetRow(iconSummary, 2);
		Grid.SetColumn(chooseBtn, 1); Grid.SetRow(chooseBtn, 2);
		Grid.SetColumn(clearBtn, 2); Grid.SetRow(clearBtn, 2);
		grid.Children.Add(nameLabel);
		grid.Children.Add(titleEntry);
		grid.Children.Add(iconSummary);
		grid.Children.Add(chooseBtn);
		grid.Children.Add(clearBtn);

		var row = new AppRow(entry.Key, iconSummary)
		{
			IconPath = string.IsNullOrWhiteSpace(iconValue) ? null : iconValue,
		};
		iconSummary.Text = DescribeIcon(row.IconPath);

		titleEntry.Unfocused += (_, _) => SaveTitle(entry.Key, titleEntry.Text);
		chooseBtn.Clicked += async (_, _) => await PickIconAsync(row);
		clearBtn.Clicked += (_, _) =>
		{
			row.IconPath = null;
			iconSummary.Text = DescribeIcon(null);
			SaveIcon(row.Key, null);
		};

		_rows.Add(row);
		IconsContainer.Children.Add(grid);
	}

	private static string DescribeIcon(string? path) =>
		string.IsNullOrWhiteSpace(path) ? "Default icon" : $"Icon: {Path.GetFileName(path)}";

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private async Task PickIconAsync(AppRow row)
	{
		try
		{
			var result = await FilePicker.Default.PickAsync(new PickOptions
			{
				PickerTitle = "Select a launcher icon (PNG)",
				FileTypes = FilePickerFileType.Images,
			});
			if (result is null)
				return;

			// Copy into app data so the path stays valid after the picker URI is released.
			var dir = Path.Combine(FileSystem.AppDataDirectory, "app-icons");
			Directory.CreateDirectory(dir);
			var baseName = string.Concat(row.Key.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
			if (string.IsNullOrWhiteSpace(baseName)) baseName = "icon";
			var dest = Path.Combine(dir, baseName + Path.GetExtension(result.FileName));
			using (var src = await result.OpenReadAsync())
			using (var dst = File.Create(dest))
				await src.CopyToAsync(dst);

			row.IconPath = dest;
			row.IconSummary.Text = DescribeIcon(dest);
			SaveIcon(row.Key, dest);
		}
		catch (Exception ex)
		{
			await DisplayAlert("App icons", $"Couldn't set the icon: {ex.Message}", "OK");
		}
	}

	private void SaveIcon(string key, string? path)
	{
		var map = LoadMap(MobileSettings.CustomAppIconsJson);
		if (string.IsNullOrWhiteSpace(path))
			map.Remove(key);
		else
			map[key] = path;
		MobileSettings.CustomAppIconsJson = JsonSerializer.Serialize(map);
	}

	private void SaveTitle(string key, string? title)
	{
		var map = LoadMap(MobileSettings.CustomAppTitlesJson);
		var t = title?.Trim();
		if (string.IsNullOrWhiteSpace(t))
			map.Remove(key);
		else
			map[key] = t;
		MobileSettings.CustomAppTitlesJson = JsonSerializer.Serialize(map);
	}

	private static Dictionary<string, string> LoadMap(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } m
				? new Dictionary<string, string>(m, StringComparer.OrdinalIgnoreCase)
				: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
		catch
		{
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
	}
}
