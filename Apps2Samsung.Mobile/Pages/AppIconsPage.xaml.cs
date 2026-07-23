using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Settings → App icons. Lets the user map an app (by a name substring of its .wgt, e.g. "Jellyfin")
/// to a custom launcher PNG; the map is persisted in <see cref="MobileSettings.CustomAppIconsJson"/>
/// and applied at install by the shared <c>CustomIconPackagePatcher</c>. Built as dynamic rows to
/// mirror the TVApp-channels editor (no data binding), for robustness.
/// </summary>
public partial class AppIconsPage : ContentPage
{
	private readonly List<IconRow> _rows = new();

	private sealed record IconRow(View Container, Entry Key, Label PathLabel)
	{
		public string? IconPath { get; set; }
	}

	public AppIconsPage()
	{
		InitializeComponent();
		foreach (var (key, value) in LoadMap())
			AddRow(key, value);
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private void OnAddIcon(object? sender, EventArgs e) => AddRow(string.Empty, string.Empty);

	private void AddRow(string key, string value)
	{
		var keyEntry = new Entry { Text = key, Placeholder = "App name (e.g. Jellyfin)", BackgroundColor = Colors.Transparent };
		var pathLabel = new Label
		{
			FontSize = 12,
			Opacity = 0.6,
			LineBreakMode = LineBreakMode.TailTruncation,
			VerticalOptions = LayoutOptions.Center,
		};
		var chooseBtn = new Button { Text = "Choose…", FontSize = 13, Padding = new Thickness(10, 0), HeightRequest = 40, CornerRadius = 6 };
		var removeBtn = new Button
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
			},
		};
		Grid.SetColumn(keyEntry, 0); Grid.SetRow(keyEntry, 0);
		Grid.SetColumn(chooseBtn, 1); Grid.SetRow(chooseBtn, 0);
		Grid.SetColumn(removeBtn, 2); Grid.SetRow(removeBtn, 0);
		Grid.SetColumn(pathLabel, 0); Grid.SetRow(pathLabel, 1); Grid.SetColumnSpan(pathLabel, 3);
		grid.Children.Add(keyEntry);
		grid.Children.Add(chooseBtn);
		grid.Children.Add(removeBtn);
		grid.Children.Add(pathLabel);

		var row = new IconRow(grid, keyEntry, pathLabel)
		{
			IconPath = string.IsNullOrWhiteSpace(value) ? null : value,
		};
		pathLabel.Text = DescribePath(row.IconPath);

		keyEntry.Unfocused += (_, _) => Save();
		chooseBtn.Clicked += async (_, _) => { await PickAsync(row); Save(); };
		removeBtn.Clicked += (_, _) =>
		{
			IconsContainer.Children.Remove(grid);
			_rows.Remove(row);
			Save();
		};

		_rows.Add(row);
		IconsContainer.Children.Add(grid);
	}

	private static string DescribePath(string? path) =>
		string.IsNullOrWhiteSpace(path) ? "No icon chosen" : $"Icon: {Path.GetFileName(path)}";

	private async Task PickAsync(IconRow row)
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
			var baseName = string.Concat((row.Key.Text ?? "icon").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
			if (string.IsNullOrWhiteSpace(baseName)) baseName = "icon";
			var dest = Path.Combine(dir, baseName + Path.GetExtension(result.FileName));
			using (var src = await result.OpenReadAsync())
			using (var dst = File.Create(dest))
				await src.CopyToAsync(dst);

			row.IconPath = dest;
			row.PathLabel.Text = DescribePath(dest);
		}
		catch (Exception ex)
		{
			await DisplayAlert("App icons", $"Couldn't set the icon: {ex.Message}", "OK");
		}
	}

	private void Save()
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var r in _rows)
		{
			var k = r.Key.Text?.Trim();
			if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(r.IconPath))
				continue;
			map[k!] = r.IconPath!;
		}
		MobileSettings.CustomAppIconsJson = JsonSerializer.Serialize(map);
	}

	private static Dictionary<string, string> LoadMap()
	{
		var json = MobileSettings.CustomAppIconsJson;
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
