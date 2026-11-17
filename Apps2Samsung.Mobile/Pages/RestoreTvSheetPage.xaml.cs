using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>One app the restore sheet handed back: the catalog entry, the file, and the list entry it came from.</summary>
public sealed record RestorePick(GitHubRelease Release, Asset Asset, Apps2Samsung.Catalog.TvProfileApp App);

/// <summary>
/// The restore sheet (#1069): what will be installed (tick, pick the build when it was a guess),
/// what cannot come back and why, and what is on the TV already. <see cref="Result"/> completes
/// with what was ticked, or null when closed.
/// </summary>
public partial class RestoreTvSheetPage : ContentPage
{
	private readonly TaskCompletionSource<IReadOnlyList<RestorePick>?> _result = new();
	private readonly List<Item> _items = new();
	private bool _chosen;

	public Task<IReadOnlyList<RestorePick>?> Result => _result.Task;

	private sealed class Item
	{
		public required Apps2Samsung.Catalog.TvProfileApp App;
		public required GitHubRelease Release;
		public required List<Asset> Builds;
		public int Build;
		public bool Guess;
		public bool Selected;
		public Border Tick = null!;
		public Label Detail = null!;
		public Button? BuildButton;
		public Asset Asset => Builds[Build];
	}

	private static Color Token(string token) => Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(token);

	public RestoreTvSheetPage(Apps2Samsung.Catalog.TvProfile profile, IReadOnlyList<Apps2Samsung.Catalog.TvRestoreItem> plan,
		IReadOnlyList<GitHubRelease> catalog, string tvLabel)
	{
		InitializeComponent();

		var from = string.IsNullOrWhiteSpace(profile.TvName) ? profile.TvIp ?? tvLabel : profile.TvName;
		TitleLabel.Text = string.Format(L10n.Get("lblRestoreTitle"), from);
		SubLabel.Text = string.Format(L10n.Get("lblRestoreSubtitle"),
			profile.SavedAtUtc.ToLocalTime().ToString("d MMMM yyyy"), profile.Apps.Count, tvLabel);

		var sources = new HashSet<string>(catalog.Select(r => r.Source.Key), StringComparer.Ordinal);
		var skipped = new List<string>();
		var blocked = 0;
		foreach (var entry in plan)
		{
			switch (entry.State)
			{
				case Apps2Samsung.Catalog.TvRestoreState.AlreadyOnTv:
					skipped.Add(entry.App.Name);
					break;
				case Apps2Samsung.Catalog.TvRestoreState.Ready when entry.Release!.IsRoot:
					AddBlocked(entry.App, L10n.Get("lblRestoreBlockedRoot"), blocked++ == 0);
					break;
				case Apps2Samsung.Catalog.TvRestoreState.Ready:
					AddReady(entry);
					break;
				default:
					var foreign = !string.IsNullOrWhiteSpace(entry.App.Source) && entry.App.Source != "official" && !sources.Contains(entry.App.Source);
					AddBlocked(entry.App, foreign
						? string.Format(L10n.Get("lblRestoreBlockedSourcePhone"), entry.App.Source)
						: L10n.Get("lblRestoreBlockedGone"), blocked++ == 0);
					break;
			}
		}

		ReadyTitle.IsVisible = ReadyCard.IsVisible = RestoreButton.IsVisible = _items.Count > 0;
		BlockedTitle.IsVisible = BlockedCard.IsVisible = blocked > 0;
		BlockedTitle.Text = string.Format(L10n.Get("lblRestoreBlockedHeader"), blocked);
		if (skipped.Count > 0)
		{
			SkippedLabel.IsVisible = true;
			SkippedLabel.Text = skipped.Count == 1
				? string.Format(L10n.Get("lblRestoreSkippedOne"), skipped[0])
				: string.Format(L10n.Get("lblRestoreSkippedMany"), string.Join(", ", skipped));
		}
		if (_items.Count == 0)
		{
			NothingLabel.IsVisible = true;
			NothingLabel.Text = L10n.Get(skipped.Count > 0 && blocked == 0 ? "lblRestoreNothingAllThere" : "lblRestoreNothing");
		}
		Count();
	}

	// The values this file asks for (#791) are there: saved on this phone, or carried by the list.
	private static bool ValuesComplete(Item item)
	{
		if (item.Asset.UserSettings is not { Fields.Count: > 0 } spec)
			return true;
		var saved = UserSettingsStore.Get(MobileSettings.AppUserSettingsJson, item.Asset.FileName)?.Values ?? item.App.Settings;
		return saved is not null && spec.Fields.All(f => UserSettingsStore.Validate(f, saved.TryGetValue(f.Key, out var v) ? v : null) is null);
	}

	private static string BuildLabel(Asset asset) => string.IsNullOrWhiteSpace(asset.Variant) ? asset.FileName : asset.Variant.Trim();

	private static string DetailOf(Item item)
	{
		var parts = new List<string>();
		var was = item.App.Version?.Trim();
		var now = item.Asset.PackageVersion?.Trim();
		if (!string.IsNullOrEmpty(was) && !string.IsNullOrEmpty(now) && !string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
			parts.Add($"{was} → {now}");
		else if (!string.IsNullOrEmpty(now ?? was))
			parts.Add(now ?? was!);
		if (item.Guess)
			parts.Add(L10n.Get("lblRestoreBuildGuess"));
		else if (!string.IsNullOrWhiteSpace(item.Asset.Variant))
			parts.Add(string.Format(L10n.Get("lblRestoreBuild"), item.Asset.Variant.Trim()));
		if (item.Asset.UserSettings is { Fields.Count: > 0 })
			parts.Add(L10n.Get(ValuesComplete(item) ? "lblRestoreValuesSaved" : "lblRestoreValuesAsked"));
		return string.Join(" · ", parts);
	}

	private void AddReady(Apps2Samsung.Catalog.TvRestoreItem entry)
	{
		var release = entry.Release!;
		// A guessed build (or one the catalog dropped) can be any of the app's files with this id.
		var guess = entry.App.Uncertain || entry.VariantChanged;
		var builds = guess
			? release.Assets.Where(a => string.Equals(a.AppId, entry.Asset!.AppId, StringComparison.OrdinalIgnoreCase)).ToList()
			: new List<Asset>();
		if (builds.Count < 2)
			builds = new List<Asset> { entry.Asset! };
		var item = new Item { App = entry.App, Release = release, Builds = builds, Guess = guess && builds.Count > 1 };
		item.Build = Math.Max(0, builds.FindIndex(a => ReferenceEquals(a, entry.Asset)));
		// An app that will stop to ask for its settings starts unticked; ticking it is saying yes to the form.
		item.Selected = ValuesComplete(item);

		var tile = new InstalledApp(release.Name, null, entry.App.AppId, string.Empty, string.Empty, true);
		item.Tick = new Border
		{
			WidthRequest = 24, HeightRequest = 24, StrokeThickness = 2, VerticalOptions = LayoutOptions.Center,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
			Content = new Label { Text = "✓", FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = Token("OnAccent"),
				HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
		};
		item.Detail = new Label { FontSize = 12, TextColor = Token("Muted"), LineBreakMode = LineBreakMode.WordWrap };
		var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
		text.Children.Add(new Label { Text = release.Name, FontSize = 15, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation });
		text.Children.Add(item.Detail);

		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(24), new ColumnDefinition(36), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
			ColumnSpacing = 10, Padding = new Thickness(12, 9), MinimumHeightRequest = 54,
		};
		grid.Add(item.Tick, 0);
		grid.Add(new Border
		{
			WidthRequest = 36, HeightRequest = 36, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 9 },
			BackgroundColor = Color.FromArgb(tile.FallbackColor),
			Content = new Label { Text = tile.Initials, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Token("OnAccent"),
				HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
		}, 1);
		grid.Add(text, 2);
		if (item.Guess)
		{
			// The build on the TV was a guess: this picks the right one.
			item.BuildButton = new Button
			{
				Text = BuildLabel(item.Asset) + " ▾", FontSize = 12, FontAttributes = FontAttributes.Bold, Padding = new Thickness(10, 0),
				HeightRequest = 32, MinimumHeightRequest = 32, CornerRadius = 16, VerticalOptions = LayoutOptions.Center,
				BackgroundColor = Token("WarnSoft"), TextColor = Token("Ink"),
			};
			item.BuildButton.Clicked += async (_, _) => await PickBuildAsync(item);
			grid.Add(item.BuildButton, 3);
		}
		var tap = new TapGestureRecognizer();
		tap.Tapped += (_, _) =>
		{
			item.Selected = !item.Selected;
			Show(item);
			Count();
		};
		grid.GestureRecognizers.Add(tap);

		if (_items.Count > 0)
			ReadyRows.Children.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(12, 0, 0, 0), Color = Token("Hair") });
		ReadyRows.Children.Add(grid);
		_items.Add(item);
		Show(item);
	}

	private async Task PickBuildAsync(Item item)
	{
		var labels = item.Builds.Select(BuildLabel).ToArray();
		var picked = await DisplayActionSheet(L10n.Get("lblRestoreChipCheckBuild"), L10n.Get("lblCancel"), null, labels);
		var index = Array.IndexOf(labels, picked);
		if (index < 0)
			return;
		item.Build = index;
		item.BuildButton!.Text = labels[index] + " ▾";
		Show(item);
	}

	private static void Show(Item item)
	{
		item.Tick.BackgroundColor = item.Selected ? Token("Accent") : Colors.Transparent;
		item.Tick.Stroke = new SolidColorBrush(item.Selected ? Token("Accent") : Token("Muted"));
		if (item.Tick.Content is Label mark)
			mark.IsVisible = item.Selected;
		item.Detail.Text = DetailOf(item);
	}

	private void AddBlocked(Apps2Samsung.Catalog.TvProfileApp app, string reason, bool first)
	{
		var tile = new InstalledApp(app.Name, null, app.AppId, string.Empty, string.Empty, true);
		var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
		text.Children.Add(new Label { Text = app.Name, FontSize = 15, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation });
		text.Children.Add(new Label { Text = reason, FontSize = 12, TextColor = Token("Muted"), LineBreakMode = LineBreakMode.WordWrap });
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(36), new ColumnDefinition(GridLength.Star) },
			ColumnSpacing = 10, Padding = new Thickness(12, 9), MinimumHeightRequest = 54,
		};
		grid.Add(new Border
		{
			WidthRequest = 36, HeightRequest = 36, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 9 },
			BackgroundColor = Token("Hair"),
			Content = new Label { Text = tile.Initials, FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Token("Muted"),
				HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
		}, 0);
		grid.Add(text, 1);
		if (!first)
			BlockedRows.Children.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(12, 0, 0, 0), Color = Token("Hair") });
		BlockedRows.Children.Add(grid);
	}

	private void Count()
	{
		var picked = _items.Count(i => i.Selected);
		ReadyTitle.Text = string.Format(L10n.Get("lblRestoreReadyHeader"), picked, _items.Count);
		RestoreButton.Text = picked == 1 ? L10n.Get("lblRestoreGoOne") : string.Format(L10n.Get("lblRestoreGoMany"), picked);
		RestoreButton.IsEnabled = picked > 0;
	}

	// Pop first, then hand back the choice (like the app sheet, #888): the caller switches tabs from it.
	private async void OnRestoreClicked(object? sender, EventArgs e)
	{
		if (_chosen)
			return;
		_chosen = true;
		var picked = _items.Where(i => i.Selected).Select(i => new RestorePick(i.Release, i.Asset, i.App)).ToList();
		try
		{
			await Navigation.PopModalAsync();
		}
		finally
		{
			_result.TrySetResult(picked);
		}
	}

	private async void OnCancelClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// The build picker covers this page for a moment; only a real close ends the sheet.
		if (!_chosen && !Navigation.ModalStack.Contains(this))
			_result.TrySetResult(null);
	}
}
