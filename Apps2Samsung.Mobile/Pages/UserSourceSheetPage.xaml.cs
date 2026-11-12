using Apps2Samsung.Catalog;
using Apps2Samsung.Catalog.Repositories;
using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Add a source of your own: a GitHub repository or release, or a direct .wgt/.tpk link, checked
/// with <see cref="UserSources.Validate"/> against the sources already there. <see cref="Result"/>
/// completes with the new <see cref="UserSource"/>, or null when the sheet is dismissed.
/// <para>A link to a signed repository (#1018) is read first, then a second step shows the
/// publisher's key fingerprint to check; only "The key matches" stores it, with the key pinned
/// (<see cref="RepositoryProbe.Approve"/>).</para>
/// </summary>
public partial class UserSourceSheetPage : ContentPage
{
	private readonly IReadOnlyList<UserSource> _existing;
	private readonly TaskCompletionSource<UserSource?> _result = new();
	// The signed repository read by Add, while its key is on screen (step 2).
	private RepositoryProbe? _probe;
	private UserSource? _probed;
	private bool _probing;
	private readonly List<string> _categories;

	public Task<UserSource?> Result => _result.Task;

	public UserSourceSheetPage(IReadOnlyList<UserSource> existing, string? fromLink = null)
	{
		InitializeComponent();
		// From an install link (#1066): filled in, and the hint says where it came from; nothing is
		// stored until Add (a signed repository goes straight to its key, as Add would).
		if (fromLink is not null)
		{
			UrlEntry.Text = fromLink;
			FormHint.Text = L10n.Get("hintAddSourceFromLink");
			if (UserSources.IsSignedRepository(fromLink))
				Loaded += async (_, _) => await ProbeAsync(Read());
		}
		_existing = existing;
		NameLabel.Text = $"{L10n.Get("lblUserSourceName")} ({L10n.Get("lblOptional")})";
		// The catalog's categories (#1054), kept as shown so the picked index maps back to its id.
		_categories = AppCategories.Ordered.ToList();
		foreach (var id in _categories)
			CategoryPicker.Items.Add(AppCategories.DisplayName(id, L10n.Get));
		CategoryPicker.SelectedIndex = Math.Max(0, _categories.IndexOf(AppCategories.Other));
		UrlEntry.Placeholder = "github.com/yourname/your-app";
	}

	/// <summary>Opens the sheet and waits for its answer.</summary>
	public static async Task<UserSource?> AskAsync(INavigation navigation, IReadOnlyList<UserSource> existing, string? fromLink = null)
	{
		var sheet = new UserSourceSheetPage(existing, fromLink);
		await navigation.PushModalAsync(sheet);
		return await sheet.Result;
	}

	private UserSource Read() => new()
	{
		Url = UrlEntry.Text?.Trim() ?? string.Empty,
		Name = NameEntry.Text?.Trim() ?? string.Empty,
		Category = CategoryPicker.SelectedIndex >= 0 && CategoryPicker.SelectedIndex < _categories.Count ? _categories[CategoryPicker.SelectedIndex] : AppCategories.Other,
		Partner = PartnerSwitch.IsToggled,
	};

	private async void OnAddClicked(object? sender, EventArgs e)
	{
		var source = Read();
		var errors = UserSources.Validate(source, _existing);
		Show(UrlError, UserSources.FieldUrl);
		Show(NameError, UserSources.FieldName);
		if (errors.Count > 0)
		{
			(errors.ContainsKey(UserSources.FieldUrl) ? UrlEntry : NameEntry).Focus();
			return;
		}
		if (UserSources.IsSignedRepository(source.Url))
		{
			await ProbeAsync(source);
			return;
		}
		_result.TrySetResult(source);
		await Navigation.PopModalAsync();

		void Show(Label error, string field)
		{
			var bad = errors.TryGetValue(field, out var key);
			error.Text = bad ? L10n.Get(key!) : string.Empty;
			error.IsVisible = bad;
		}
	}

	// Reads the repository and its key; a failure says why under the link, a success shows step 2.
	private async Task ProbeAsync(UserSource source)
	{
		if (_probing)
			return;
		_probing = true;
		AddBtn.IsEnabled = false;
		AddBtn.Text = L10n.Get("lblRepoReading");
		try
		{
			var repositories = IPlatformApplication.Current!.Services.GetRequiredService<CatalogService>().Repositories;
			var probe = await repositories.ProbeAsync(source.Url);
			if (!probe.Ok || probe.Manifest is null || string.IsNullOrEmpty(probe.Fingerprint))
			{
				UrlError.Text = string.Format(L10n.Get("errRepoNotAdded"), probe.Message ?? probe.Reason);
				UrlError.IsVisible = true;
				return;
			}
			_probe = probe;
			_probed = source;
			ShowKey(probe);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[sources] Probe failed: {ex}");
			UrlError.Text = string.Format(L10n.Get("errRepoNotAdded"), ex.Message);
			UrlError.IsVisible = true;
		}
		finally
		{
			_probing = false;
			AddBtn.IsEnabled = true;
			AddBtn.Text = L10n.Get("lblAdd");
		}
	}

	// Step 2 (mockup ApproveB): the fingerprint as 16 groups of four, who publishes it and from where.
	private void ShowKey(RepositoryProbe probe)
	{
		FingerprintGrid.Clear();
		FingerprintGrid.RowDefinitions.Clear();
		var groups = RepositoryVerifier.FormatFingerprint(probe.Fingerprint!).Split(' ', StringSplitOptions.RemoveEmptyEntries);
		for (var i = 0; i < groups.Length; i++)
		{
			if (i % 4 == 0)
				FingerprintGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			FingerprintGrid.Add(new Label
			{
				Text = groups[i], FontFamily = "monospace", FontSize = 17, CharacterSpacing = 0.6,
				HorizontalTextAlignment = TextAlignment.Center,
			}, i % 4, i / 4);
		}
		SemanticProperties.SetDescription(FingerprintGrid, RepositoryVerifier.FormatFingerprint(probe.Fingerprint!));

		var repository = probe.Manifest!.Repository;
		var name = string.IsNullOrWhiteSpace(_probed?.Name) ? repository.Name : _probed!.Name;
		RepoLetter.Text = AppTiles.Letter(name);
		RepoTile.BackgroundColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.TileColor(AppCategories.Other, name);
		RepoName.Text = name;
		RepoBy.Text = string.Format(L10n.Get("lblRepoBy"), repository.Maintainer, probe.Manifest.Providers.Count);
		RepoDomain.Text = probe.Domain;

		FormStep.IsVisible = false;
		KeyStep.IsVisible = true;
	}

	private async void OnCopyFingerprintClicked(object? sender, EventArgs e)
	{
		if (_probe?.Fingerprint is not { } fingerprint)
			return;
		await Clipboard.Default.SetTextAsync(RepositoryVerifier.FormatFingerprint(fingerprint));
		CopyFingerprintBtn.Text = L10n.Get("lblCopied");
		await Task.Delay(1500);
		CopyFingerprintBtn.Text = L10n.Get("lblRepoCopyFingerprint");
	}

	// "The key matches, add it": the source stored with this key pinned.
	private async void OnKeyMatchesClicked(object? sender, EventArgs e)
	{
		if (_probe is null || _probed is null)
			return;
		_result.TrySetResult(_probe.Approve(_probed.Url, _probed.Name));
		await Navigation.PopModalAsync();
	}

	private async void OnCancelClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	// Swiped away or cancelled: the caller gets null. The app going to the background raises this
	// too, with the sheet still up, so only a sheet that is gone from the stack answers.
	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!Navigation.ModalStack.Contains(this))
			_result.TrySetResult(null);
	}
}
