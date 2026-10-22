using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > App customization > Camera PiP: cameras, mosaic style and sound.</summary>
public partial class SettingsCameraPipPage : ContentPage
{
	private bool _loaded;
	private readonly List<CameraRow> _cameraRows = new();

	private sealed record CameraRow(View Container, Entry Name, Entry VideoUrl, Entry MjpegUrl);

	public SettingsCameraPipPage()
	{
		InitializeComponent();
		TopBar.Title = L10n.Get("lblCameraPip");
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_loaded = false;
		LoadCameraPip();
		_loaded = true;
	}

	// The same small square button the TVApp page uses for its rows.
	private static Button SmallChannelButton(string text, string textColorToken) => SettingsTvAppPage.SmallChannelButton(text, textColorToken);

	// ---- Camera PiP ----
	// One to four cameras (name + HLS video URL + MJPEG URL), the mosaic style and sound, persisted
	// as the JSON the shared CameraPipConfigInjector reads at install (mirrors the desktop head).

	private void LoadCameraPip()
	{
		var settings = CameraPipConfigInjector.ParseSettingsJson(MobileSettings.CameraPipConfigJson);

		CamerasContainer.Children.Clear();
		_cameraRows.Clear();
		foreach (var camera in settings.Cameras.Take(CameraPipSettings.MaxCameras))
			AddCameraRow(camera.Name, camera.VideoUrl, camera.MjpegUrl);
		UpdateAddCameraButton();

		// Index 0 = tiled, 1 = overlay — the order of CameraPipMosaicValues.
		CameraPipMosaicPicker.ItemsSource = new List<string>
		{
			L10n.Get("lblCameraPipMosaicTiled"),
			L10n.Get("lblCameraPipMosaicOverlay"),
		};
		CameraPipMosaicPicker.SelectedIndex = settings.MosaicStyle == CameraPipSettings.MosaicOverlay ? 1 : 0;
		CameraPipSoundSwitch.IsToggled = settings.Sound;
	}

	private static readonly string[] CameraPipMosaicValues = { CameraPipSettings.MosaicTiled, CameraPipSettings.MosaicOverlay };

	private void OnAddCamera(object? sender, EventArgs e)
	{
		AddCameraRow(string.Empty, string.Empty, string.Empty);
		UpdateAddCameraButton();
	}

	private void OnCameraPipMosaicChanged(object? sender, EventArgs e) => SaveCameraPip();

	private void OnCameraPipSoundToggled(object? sender, ToggledEventArgs e) => SaveCameraPip();

	// The app composes at most four cameras, so the button goes away at the limit.
	private void UpdateAddCameraButton() => AddCameraBtn.IsVisible = _cameraRows.Count < CameraPipSettings.MaxCameras;

	private void AddCameraRow(string name, string videoUrl, string mjpegUrl)
	{
		if (_cameraRows.Count >= CameraPipSettings.MaxCameras)
			return;

		var nameEntry = new Entry { Text = name, Placeholder = L10n.Get("lblCameraName"), BackgroundColor = Colors.Transparent };
		var videoEntry = new Entry { Text = videoUrl, Placeholder = "http://…:8090/front.m3u8", Keyboard = Keyboard.Url, BackgroundColor = Colors.Transparent };
		var mjpegEntry = new Entry { Text = mjpegUrl, Placeholder = "http://…:5000/api/front?h=360&fps=3", Keyboard = Keyboard.Url, BackgroundColor = Colors.Transparent };
		var removeBtn = SmallChannelButton("✕", "Bad");

		// Row 0: name + remove. Rows 1 and 2: the two stream URLs, full width.
		var grid = new Grid
		{
			ColumnSpacing = 8,
			RowSpacing = 2,
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto),
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
			},
		};
		Grid.SetColumn(nameEntry, 0); Grid.SetRow(nameEntry, 0);
		Grid.SetColumn(removeBtn, 1); Grid.SetRow(removeBtn, 0);
		Grid.SetColumn(videoEntry, 0); Grid.SetRow(videoEntry, 1); Grid.SetColumnSpan(videoEntry, 2);
		Grid.SetColumn(mjpegEntry, 0); Grid.SetRow(mjpegEntry, 2); Grid.SetColumnSpan(mjpegEntry, 2);

		grid.Children.Add(nameEntry);
		grid.Children.Add(removeBtn);
		grid.Children.Add(videoEntry);
		grid.Children.Add(mjpegEntry);

		var row = new CameraRow(grid, nameEntry, videoEntry, mjpegEntry);
		nameEntry.Unfocused += (_, _) => SaveCameraPip();
		videoEntry.Unfocused += (_, _) => SaveCameraPip();
		mjpegEntry.Unfocused += (_, _) => SaveCameraPip();
		removeBtn.Clicked += (_, _) =>
		{
			CamerasContainer.Children.Remove(grid);
			_cameraRows.Remove(row);
			UpdateAddCameraButton();
			SaveCameraPip();
		};

		_cameraRows.Add(row);
		CamerasContainer.Children.Add(grid);
	}

	private void SaveCameraPip()
	{
		if (!_loaded)
			return;

		var cameras = _cameraRows
			.Select(r => new CameraPipCamera(r.Name.Text ?? string.Empty, r.VideoUrl.Text ?? string.Empty, r.MjpegUrl.Text ?? string.Empty))
			.ToList();
		var index = CameraPipMosaicPicker.SelectedIndex;
		var mosaic = index >= 0 && index < CameraPipMosaicValues.Length ? CameraPipMosaicValues[index] : CameraPipSettings.MosaicTiled;

		MobileSettings.CameraPipConfigJson = CameraPipConfigInjector.ToJson(
			new CameraPipSettings(cameras, mosaic, CameraPipSoundSwitch.IsToggled));
	}
}
