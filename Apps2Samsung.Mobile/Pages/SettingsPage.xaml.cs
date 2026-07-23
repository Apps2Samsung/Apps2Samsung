using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
	private bool _loaded;
	private readonly List<ChannelRow> _channelRows = new();

	private sealed record ChannelRow(View Container, Entry Name, Entry Url);

	public SettingsPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		// Load current values without firing the change handlers.
		_loaded = false;
		TokenEntry.Text = MobileSettings.GitHubToken;
		DuidsEditor.Text = MobileSettings.ManualDuids;
		RemoveOldSwitch.IsToggled = MobileSettings.DeletePreviousInstall;
		OpenAfterSwitch.IsToggled = MobileSettings.OpenAfterInstall;
		KeepWgtSwitch.IsToggled = MobileSettings.KeepWgtFile;
		ShowAllJfSwitch.IsToggled = MobileSettings.ShowAllJellyfinVersions;
		PartnerSigningSwitch.IsToggled = MobileSettings.PartnerSigning;

		ChannelsContainer.Children.Clear();
		_channelRows.Clear();
		foreach (var channel in MobileSettings.GetTvAppChannels())
			AddChannelRow(channel.Name, channel.Url);

		_loaded = true;
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private async void OnAppIconsClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new AppIconsPage());

	private async void OnJellyfinClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new JellyfinSettingsPage());

	// Hands the current session's debug log to the OS share sheet. The file lives in private app
	// storage (FileSystem.AppDataDirectory/Logs) with no way to reach it otherwise, so this is how a
	// user gets logs off the phone when reporting an issue. Saving is set up in MauiProgram via FileLog.
	private async void OnShareLogClicked(object? sender, EventArgs e)
	{
		var logPath = Apps2Samsung.Diagnostics.FileLog.CurrentLogFile;
		if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
		{
			await DisplayAlert("Debug log", "No log file is available for this session yet.", "OK");
			return;
		}

		try
		{
			await Share.Default.RequestAsync(new ShareFileRequest
			{
				Title = "Apps2Samsung debug log",
				File = new ShareFile(logPath),
			});
		}
		catch (Exception ex)
		{
			await DisplayAlert("Debug log", $"Couldn't share the log: {ex.Message}", "OK");
		}
	}

	private void OnToggleTokenVisibility(object? sender, EventArgs e)
	{
		TokenEntry.IsPassword = !TokenEntry.IsPassword;
		TokenEyeBtn.Opacity = TokenEntry.IsPassword ? 1.0 : 0.5;
	}

	private async void OnTokenUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			await MobileSettings.SetGitHubTokenAsync(TokenEntry.Text);
	}

	private void OnDuidsUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			MobileSettings.ManualDuids = DuidsEditor.Text ?? string.Empty;
	}

	private void OnToggle(object? sender, ToggledEventArgs e)
	{
		if (!_loaded)
			return;

		MobileSettings.DeletePreviousInstall = RemoveOldSwitch.IsToggled;
		MobileSettings.OpenAfterInstall = OpenAfterSwitch.IsToggled;
		MobileSettings.KeepWgtFile = KeepWgtSwitch.IsToggled;
		MobileSettings.ShowAllJellyfinVersions = ShowAllJfSwitch.IsToggled;
		MobileSettings.PartnerSigning = PartnerSigningSwitch.IsToggled;
	}

	private void OnAddChannel(object? sender, EventArgs e) => AddChannelRow(string.Empty, string.Empty);

	private void AddChannelRow(string name, string url)
	{
		var nameEntry = new Entry { Text = name, Placeholder = "Name", BackgroundColor = Colors.Transparent };
		var urlEntry = new Entry { Text = url, Placeholder = "https://…/stream.m3u8", BackgroundColor = Colors.Transparent };
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
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(new GridLength(40)),
			},
		};
		Grid.SetColumn(nameEntry, 0);
		Grid.SetColumn(urlEntry, 1);
		Grid.SetColumn(removeBtn, 2);
		grid.Children.Add(nameEntry);
		grid.Children.Add(urlEntry);
		grid.Children.Add(removeBtn);

		var row = new ChannelRow(grid, nameEntry, urlEntry);
		nameEntry.Unfocused += (_, _) => SaveChannels();
		urlEntry.Unfocused += (_, _) => SaveChannels();
		removeBtn.Clicked += (_, _) =>
		{
			ChannelsContainer.Children.Remove(grid);
			_channelRows.Remove(row);
			SaveChannels();
		};

		_channelRows.Add(row);
		ChannelsContainer.Children.Add(grid);
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
