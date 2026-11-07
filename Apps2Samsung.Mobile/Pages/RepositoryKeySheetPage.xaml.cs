using Apps2Samsung.Catalog;
using Apps2Samsung.Catalog.Repositories;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the key-changed sheet decided: the source with the new key pinned, or remove it.</summary>
/// <param name="Trusted">The repository read again and approved with its new key; replaces the stored one.</param>
/// <param name="Remove">"Remove source".</param>
public sealed record RepositoryKeyChoice(UserSource? Trusted, bool Remove);

/// <summary>
/// A signed repository whose publisher key changed (#1018): its apps stay hidden until the user
/// trusts the new key or removes the source. Trust reads the repository again and only takes the
/// key it finds when that is still the one shown here. <see cref="Result"/> is null when closed.
/// </summary>
public partial class RepositoryKeySheetPage : ContentPage
{
	private readonly UserSource _source;
	// The key on screen as "Now"; only that one is trusted.
	private string _newFingerprint;
	private readonly TaskCompletionSource<RepositoryKeyChoice?> _result = new();
	private bool _working;

	public Task<RepositoryKeyChoice?> Result => _result.Task;

	public RepositoryKeySheetPage(UserSource source, string newFingerprint)
	{
		InitializeComponent();
		_source = source;
		_newFingerprint = newFingerprint;
		BodyLabel.Text = string.Format(L10n.Get("hintRepoKeyChanged"), UserSources.DisplayName(source));
		WasLabel.Text = string.IsNullOrEmpty(source.Fingerprint) ? string.Empty : RepositoryVerifier.FormatFingerprint(source.Fingerprint);
		NowLabel.Text = RepositoryVerifier.FormatFingerprint(newFingerprint);
	}

	/// <summary>Opens the sheet and waits for its answer.</summary>
	public static async Task<RepositoryKeyChoice?> AskAsync(INavigation navigation, UserSource source, string newFingerprint)
	{
		var sheet = new RepositoryKeySheetPage(source, newFingerprint);
		await navigation.PushModalAsync(sheet);
		return await sheet.Result;
	}

	// Reads the repository again: its key must still be the new one shown, and the file must verify with it.
	private async void OnTrustClicked(object? sender, EventArgs e)
	{
		if (_working)
			return;
		_working = true;
		TrustBtn.IsEnabled = false;
		TrustBtn.Text = L10n.Get("lblRepoReading");
		ErrorLabel.IsVisible = false;
		try
		{
			var repositories = IPlatformApplication.Current!.Services.GetRequiredService<CatalogService>().Repositories;
			var probe = await repositories.ProbeAsync(_source.Url);
			if (!probe.Ok || probe.Manifest is null || string.IsNullOrEmpty(probe.Fingerprint))
			{
				ErrorLabel.Text = string.Format(L10n.Get("errRepoNotAdded"), probe.Message ?? probe.Reason);
				ErrorLabel.IsVisible = true;
				return;
			}
			if (RepositoryVerifier.NormalizeFingerprint(probe.Fingerprint) != RepositoryVerifier.NormalizeFingerprint(_newFingerprint))
			{
				// Changed again since the catalog load: show the key that is there now; only a next
				// tap, with that one on screen, trusts it.
				_newFingerprint = probe.Fingerprint;
				NowLabel.Text = RepositoryVerifier.FormatFingerprint(probe.Fingerprint);
				return;
			}
			await AnswerAsync(new RepositoryKeyChoice(probe.Approve(_source.Url, _source.Name), false));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[sources] Re-reading {_source.Url} failed: {ex}");
			ErrorLabel.Text = string.Format(L10n.Get("errRepoNotAdded"), ex.Message);
			ErrorLabel.IsVisible = true;
		}
		finally
		{
			_working = false;
			TrustBtn.IsEnabled = true;
			TrustBtn.Text = L10n.Get("lblRepoTrustNewKey");
		}
	}

	private async void OnRemoveClicked(object? sender, EventArgs e) => await AnswerAsync(new RepositoryKeyChoice(null, true));

	private async void OnCancelClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	private async Task AnswerAsync(RepositoryKeyChoice choice)
	{
		_result.TrySetResult(choice);
		await Navigation.PopModalAsync();
	}

	// Swiped away or cancelled: the caller gets null. Only a sheet that is gone from the stack answers
	// (the app going to the background raises this too).
	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!Navigation.ModalStack.Contains(this))
			_result.TrySetResult(null);
	}
}
