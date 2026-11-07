using Apps2Samsung.Mobile.Localization;
using System.Text.Json;
using Apps2Samsung.Collections;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > App customization > TVApp: the channels written into the package, in play order.</summary>
public partial class SettingsTvAppPage : ContentPage
{
	private bool _loaded;
	private readonly List<ChannelRow> _channelRows = new();

	private sealed record ChannelRow(View Container, Entry Name, Entry Url);

	public SettingsTvAppPage()
	{
		InitializeComponent();
		TopBar.Title = "TVApp";
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_loaded = false;
		ChannelsContainer.Children.Clear();
		_channelRows.Clear();
		foreach (var channel in MobileSettings.GetTvAppChannels())
			AddChannelRow(channel.Name, channel.Url);
		_loaded = true;
	}

	private void OnAddChannel(object? sender, EventArgs e) => AddChannelRow(string.Empty, string.Empty);

	private void AddChannelRow(string name, string url)
	{
		var nameEntry = new Entry { Text = name, Placeholder = L10n.Get("lblChannelName"), BackgroundColor = Colors.Transparent };
		var urlEntry = new Entry { Text = url, Placeholder = "https://…/stream.m3u8", BackgroundColor = Colors.Transparent };
		var upBtn = SmallChannelButton("↑", "Accent");
		var downBtn = SmallChannelButton("↓", "Accent");
		var removeBtn = SmallChannelButton("✕", "Bad");

		// Row 0: name + url. Row 1: reorder (↑ ↓) + remove, right-aligned, so the entries stay wide.
		var grid = new Grid
		{
			ColumnSpacing = 8,
			RowSpacing = 2,
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
			},
		};
		Grid.SetColumn(nameEntry, 0); Grid.SetRow(nameEntry, 0);
		Grid.SetColumn(urlEntry, 1); Grid.SetRow(urlEntry, 0);

		var buttons = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.End };
		buttons.Children.Add(upBtn);
		buttons.Children.Add(downBtn);
		buttons.Children.Add(removeBtn);
		Grid.SetColumn(buttons, 0); Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 2);

		grid.Children.Add(nameEntry);
		grid.Children.Add(urlEntry);
		grid.Children.Add(buttons);

		var row = new ChannelRow(grid, nameEntry, urlEntry);
		nameEntry.Unfocused += (_, _) => SaveChannels();
		urlEntry.Unfocused += (_, _) => SaveChannels();
		// Channels play on the TV in list order, so let the user reorder them (mirrors the desktop head).
		upBtn.Clicked += (_, _) => MoveChannelRow(row, -1);
		downBtn.Clicked += (_, _) => MoveChannelRow(row, +1);
		removeBtn.Clicked += (_, _) =>
		{
			ChannelsContainer.Children.Remove(grid);
			_channelRows.Remove(row);
			SaveChannels();
		};

		_channelRows.Add(row);
		ChannelsContainer.Children.Add(grid);
	}

	internal static Button SmallChannelButton(string text, string textColorToken) => new()
	{
		Text = text,
		FontSize = 14,
		Padding = 0,
		WidthRequest = 40,
		HeightRequest = 40,
		CornerRadius = 6,
		BackgroundColor = Colors.Transparent,
		BorderWidth = 1,
		BorderColor = DesignTheme.ColorOf("Hair"),
		TextColor = DesignTheme.ColorOf(textColorToken),
	};

	private void MoveChannelRow(ChannelRow row, int delta)
	{
		var i = _channelRows.IndexOf(row);
		// Shared bounds rule with the desktop head (Core Collections.ListReorder).
		if (ListReorder.TargetIndex(_channelRows.Count, i, delta) is not int j)
			return;

		_channelRows.RemoveAt(i);
		_channelRows.Insert(j, row);

		// Rebuild the visual order to match, then persist.
		ChannelsContainer.Children.Clear();
		foreach (var r in _channelRows)
			ChannelsContainer.Children.Add(r.Container);

		SaveChannels();
	}

	private void SaveChannels()
	{
		if (!_loaded)
			return;

		// Persist as the {name,url} JSON shape the Core injector expects; skip blank rows.
		var channels = _channelRows
			.Select(r => new { name = r.Name.Text ?? string.Empty, url = r.Url.Text ?? string.Empty })
			.Where(c => !string.IsNullOrWhiteSpace(c.name) || !string.IsNullOrWhiteSpace(c.url))
			.ToList();

		MobileSettings.TvAppChannelsJson = JsonSerializer.Serialize(channels);
	}
}
