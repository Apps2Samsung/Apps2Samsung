using Apps2Samsung.Catalog;
using Apps2Samsung.Catalog.Repositories;
using Apps2Samsung.Certificate;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > Installing: the signing certificate and the options every install follows.</summary>
public partial class SettingsInstallPage : ContentPage
{
	private bool _loaded;

	public SettingsInstallPage()
	{
		InitializeComponent();
		// Public and Partner are Samsung's names for the two certificate levels, shown through the
		// same keys as the certificate sheet's level pill (not the stored preference values).
		CertPublic.Text = L10n.Get("certLevelPublic");
		CertPartner.Text = L10n.Get("certLevelPartner");
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		// Load current values without firing the change handlers.
		_loaded = false;
		ShowCertificateKind();
		ShowCertificate(MobileSettings.CertificatePreference);
		ForceLoginSwitch.IsToggled = MobileSettings.ForceSamsungLogin;
		RemoveOldSwitch.IsToggled = MobileSettings.DeletePreviousInstall;
		TryOverwriteSwitch.IsToggled = MobileSettings.TryOverwrite;
		OpenAfterSwitch.IsToggled = MobileSettings.OpenAfterInstall;
		KeepWgtSwitch.IsToggled = MobileSettings.KeepWgtFile;
		_loaded = true;
		ShowSources();
	}

	// Your sources: one row per stored source, the name over what it points at and how it is doing
	// (#1018: a signed repository's last check, with a coloured dot; "Unverified · not signed"
	// otherwise), with Remove. A repository whose key changed gets Review instead: the key-changed
	// sheet, which offers Remove too.
	private void ShowSources()
	{
		SourceRows.Clear();
		var sources = UserSources.Parse(MobileSettings.UserSourcesJson);
		NoSourcesLabel.IsVisible = sources.Count == 0;
		foreach (var source in sources)
		{
			var repository = UserSources.IsSignedRepository(source.Url);
			var detail = repository ? source.Url.Trim()
				: UserSources.IsFeed(source.Url)
				? $"{source.Url.Trim()} · {L10n.Get("lblUserSourceFeed")}"
				: $"{source.Url.Trim()} · {AppCategories.DisplayName(source.Category, L10n.Get)}";
			if (source.Partner)
				detail += $" · {L10n.Get("certLevelPartner")}";
			var (statusText, state) = CatalogSourceLabels.Status(source, L10n.Get);
			var keyChanged = state == RepositoryState.KeyChanged && SignedRepositories.LastLoad(source.Url)?.NewFingerprint is not null;
			var action = new Button
			{
				Text = L10n.Get(keyChanged ? "lblRepoReview" : "lblRemove"), Style = (Style)Application.Current!.Resources["SoftButton"], CornerRadius = 10,
				VerticalOptions = LayoutOptions.Center,
			};
			var captured = source;
			action.Clicked += async (_, _) =>
			{
				if (keyChanged)
					await ReviewKeyAsync(captured);
				else
					await RemoveSourceAsync(captured);
			};
			var row = new Grid { Style = (Style)Application.Current!.Resources["SettingRow"] };
			row.Add(new VerticalStackLayout
			{
				Spacing = 2, VerticalOptions = LayoutOptions.Center,
				Children =
				{
					new Label { Text = UserSources.DisplayName(source), FontSize = 16, LineBreakMode = LineBreakMode.TailTruncation },
					StatusLine(statusText, state),
					new Label { Text = detail, Style = (Style)Application.Current!.Resources["RowHint"], LineBreakMode = LineBreakMode.TailTruncation },
				},
			}, 0);
			row.Add(action, 1);
			SourceRows.Add(row);
			SourceRows.Add(new BoxView { Style = (Style)Application.Current!.Resources["RowDivider"] });
		}
	}

	// "Signed · verified" after a green dot, a stale copy amber, a changed key or a failed check red;
	// an unverified source has no dot (#1018, mockup KeyChanged).
	private static View StatusLine(string text, RepositoryState? state)
	{
		var token = state switch
		{
			RepositoryState.Ok => "Ok",
			RepositoryState.Stale => "Warn",
			RepositoryState.KeyChanged or RepositoryState.Failed => "Bad",
			_ => null,
		};
		var label = new Label
		{
			Text = text, FontSize = 13, LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center,
			TextColor = DesignTheme.ColorOf(token is "Bad" or "Warn" ? token : "Muted"),
		};
		if (token is null)
			return label;
		var dot = new Microsoft.Maui.Controls.Shapes.Ellipse
		{
			Fill = new SolidColorBrush(DesignTheme.ColorOf(token)), WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center,
		};
		return new HorizontalStackLayout { Spacing = 6, Children = { dot, label } };
	}

	// The key-changed sheet: trusting the new key replaces the stored source with the newly
	// approved one; Remove removes it. The catalog reads the sources again on its next appearance.
	private async Task ReviewKeyAsync(UserSource source)
	{
		if (SignedRepositories.LastLoad(source.Url)?.NewFingerprint is not { } fingerprint)
			return;
		var choice = await RepositoryKeySheetPage.AskAsync(Navigation, source, fingerprint);
		if (choice is null)
			return;
		var sources = UserSources.Parse(MobileSettings.UserSourcesJson);
		var index = sources.FindIndex(s => string.Equals(s.Url.Trim(), source.Url.Trim(), StringComparison.OrdinalIgnoreCase));
		if (choice.Remove || choice.Trusted is null)
		{
			if (index >= 0)
				sources.RemoveAt(index);
		}
		else
		{
			var trusted = choice.Trusted;
			trusted.Category = source.Category;
			trusted.Partner = source.Partner;
			if (index >= 0)
				sources[index] = trusted;
			else
				sources.Add(trusted);
			// Checked now with the new key, so the row says how it is doing instead of "key changed".
			try
			{
				var repositories = IPlatformApplication.Current!.Services.GetRequiredService<Apps2Samsung.Mobile.Catalog.CatalogService>().Repositories;
				await repositories.LoadAsync(trusted);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine($"[sources] Check after trusting the new key failed: {ex.Message}");
			}
		}
		MobileSettings.UserSourcesJson = UserSources.Serialize(sources);
		ShowSources();
	}

	private async void OnAddSourceClicked(object? sender, EventArgs e)
	{
		var sources = UserSources.Parse(MobileSettings.UserSourcesJson);
		var added = await UserSourceSheetPage.AskAsync(Navigation, sources);
		if (added is null)
			return;
		sources.Add(added);
		MobileSettings.UserSourcesJson = UserSources.Serialize(sources);
		ShowSources();
	}

	private async Task RemoveSourceAsync(UserSource source)
	{
		var name = UserSources.DisplayName(source);
		if (!await DisplayAlert(L10n.Get("lblRemoveSourceTitle"), string.Format(L10n.Get("lblRemoveSourceBody"), name), L10n.Get("lblRemove"), L10n.Get("lblCancel")))
			return;
		var sources = UserSources.Parse(MobileSettings.UserSourcesJson);
		var api = UserSources.ApiUrl(source.Url);
		sources.RemoveAll(s => string.Equals(UserSources.ApiUrl(s.Url), api, StringComparison.OrdinalIgnoreCase));
		MobileSettings.UserSourcesJson = UserSources.Serialize(sources);
		ShowSources();
	}

	// Samsung / Tizen (#988): the chosen row on the soft accent, its radio filled and ticked.
	private void ShowCertificateKind()
	{
		var tizen = MobileSettings.UsesTizenCertificate;
		ShowPick(SamsungRow, SamsungRadio, SamsungCheck, SamsungLabel, !tizen);
		ShowPick(TizenRow, TizenRadio, TizenCheck, TizenLabel, tizen);
		// The level hint for the chosen kind: Samsung's mentions the account, Tizen's does not.
		LevelHintSamsung.IsVisible = !tizen;
		LevelHintTizen.IsVisible = tizen;
		ShowCertificateStatus();
	}

	private static void ShowPick(Border row, Border radio, Microsoft.Maui.Controls.Shapes.Path check, Label label, bool on)
	{
		row.BackgroundColor = on ? DesignTheme.ColorOf("AccentSoft") : Colors.Transparent;
		radio.BackgroundColor = on ? DesignTheme.ColorOf("Accent") : Colors.Transparent;
		radio.Stroke = on ? new SolidColorBrush(DesignTheme.ColorOf("Accent")) : new SolidColorBrush(DesignTheme.ColorOf("Hair"));
		check.IsVisible = on;
		label.FontFamily = on ? "OpenSansSemibold" : "OpenSansRegular";
	}

	private void OnSamsungTapped(object? sender, TappedEventArgs e) => SelectKind(MobileSettings.CertificateKindSamsung);

	private void OnTizenTapped(object? sender, TappedEventArgs e) => SelectKind(MobileSettings.CertificateKindTizen);

	private void SelectKind(string kind)
	{
		MobileSettings.CertificateKind = kind;
		ShowCertificateKind();
	}

	// What the store holds for the chosen certificate and level: the Samsung profile's expiry and
	// the TVs it covers, or the Tizen profile's expiry; "none yet" otherwise.
	private void ShowCertificateStatus()
	{
		try
		{
			var store = new MobileAppConfig().CertificateStorePath;
			var preference = MobileSettings.CertificatePreference;
			if (MobileSettings.UsesTizenCertificate)
			{
				var level = Apps2Samsung.Certificate.CertificateKind.TizenLevel(preference, false);
				var dir = TizenProfileStore.ProfileDir(store, level);
				var until = TizenProfileStore.HasProfile(store, level) ? CertificateProvisioningService.AuthorValidUntil(dir) : null;
				CertificateStatus.Text = until is { } u
					? string.Format(L10n.Get("certStatusTizen"), level, u.ToString("d"))
					: string.Format(L10n.Get("certStatusTizenNone"), level);
			}
			else
			{
				var level = Apps2Samsung.Certificate.CertificatePreference.ResolveLevel(preference, false);
				var dir = Path.Combine(store, CertificateProvisioningService.ProfileName(level));
				var until = CertificateProvisioningService.HasProfile(store, level) ? CertificateProvisioningService.AuthorValidUntil(dir) : null;
				CertificateStatus.Text = until is { } u
					? string.Format(L10n.Get("certStatusSamsung"), level, u.ToString("d"), CertificateProvisioningService.GetCoveredDuids(dir).Count)
					: string.Format(L10n.Get("certStatusSamsungNone"), level);
			}
			CertificateStatus.IsVisible = true;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[settings] Certificate status: {ex.Message}");
			CertificateStatus.IsVisible = false;
		}
	}

	// Automatic / Public / Partner, the one in force on the surface colour.
	private void ShowCertificate(string preference)
	{
		CertAuto.BackgroundColor = Segment(preference == MobileSettings.CertificatePreferenceAuto);
		CertPublic.BackgroundColor = Segment(preference == MobileSettings.CertificatePreferencePublic);
		CertPartner.BackgroundColor = Segment(preference == MobileSettings.CertificatePreferencePartner);
		if (_loaded)
			ShowCertificateStatus();
	}

	private static Color Segment(bool on) => on ? DesignTheme.ColorOf("Surface") : Colors.Transparent;

	private void OnCertificateClicked(object? sender, EventArgs e)
	{
		var preference = sender == CertPublic ? MobileSettings.CertificatePreferencePublic
			: sender == CertPartner ? MobileSettings.CertificatePreferencePartner
			: MobileSettings.CertificatePreferenceAuto;
		MobileSettings.CertificatePreference = preference;
		ShowCertificate(preference);
	}

	private void OnToggle(object? sender, ToggledEventArgs e)
	{
		if (!_loaded)
			return;

		MobileSettings.ForceSamsungLogin = ForceLoginSwitch.IsToggled;
		MobileSettings.DeletePreviousInstall = RemoveOldSwitch.IsToggled;
		MobileSettings.TryOverwrite = TryOverwriteSwitch.IsToggled;
		MobileSettings.OpenAfterInstall = OpenAfterSwitch.IsToggled;
		MobileSettings.KeepWgtFile = KeepWgtSwitch.IsToggled;
	}

	// Makes the certificate for the selected TV now, so the first install doesn't stop for a
	// Samsung sign-in. Same flow as an install's certificate step, sheet included.
	private async void OnPrepareClicked(object? sender, EventArgs e)
	{
		var services = IPlatformApplication.Current!.Services;
		var tv = services.GetRequiredService<TvSelection>();
		var flow = services.GetRequiredService<CertificateFlow>();

		// A Tizen certificate (#988) names no TV: made right here, whether or not one is selected.
		// Choosing a Samsung certificate on its sheet switches the setting and goes on below.
		if (MobileSettings.UsesTizenCertificate)
		{
			PrepareBtn.IsEnabled = false;
			try
			{
				var tizen = await flow.RunTizenAsync(Navigation, tv.HasTv ? tv.DisplayName : null, false, null, new CancellationTokenSource());
				if (tizen is { } made)
				{
					await DisplayAlert(L10n.Get("lblStepCertificate"),
						L10n.Get(made.Created ? "statusTizenCertificatePrepared" : "statusTizenCertificateAlreadyReady"), L10n.Get("btn_Close"));
					return;
				}
				ShowCertificateKind();
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				if (!Navigation.ModalStack.OfType<CertificateSheetPage>().Any())
					await DisplayAlert(L10n.Get("lblStepCertificate"), ErrorText.Describe(ex, "certificate"), L10n.Get("btn_Close"));
				return;
			}
			finally
			{
				PrepareBtn.IsEnabled = true;
				ShowCertificateStatus();
			}
		}

		if (!tv.HasTv)
		{
			await DisplayAlert(L10n.Get("lblStepCertificate"), L10n.Get("statusSelectTvFirst"), L10n.Get("btn_Close"));
			return;
		}

		PrepareBtn.IsEnabled = false;
		PrepareBtn.Text = L10n.Get("statusReadingTvDuid");
		try
		{
			var (_, created) = await flow.RunAsync(Navigation, tv.Ip!, tv.DisplayName, false, null, new CancellationTokenSource());
			await DisplayAlert(
				L10n.Get("lblStepCertificate"),
				string.Format(L10n.Get(created ? "statusCertificatePrepared" : "statusCertificateAlreadyReady"), tv.DisplayName),
				L10n.Get("btn_Close"));
		}
		catch (OperationCanceledException)
		{
			// Cancel on the sheet, or Samsung's sign-in page closed: nothing to report.
		}
		catch (SamsungAccountEmailMissingException)
		{
			await Navigation.PushModalAsync(new AccountEmailSheetPage());
		}
		catch (Exception ex)
		{
			// The sheet shows the reason when it got that far; before it (reading the TV) it didn't open.
			if (!Navigation.ModalStack.OfType<CertificateSheetPage>().Any())
				await DisplayAlert(L10n.Get("lblStepCertificate"), ErrorText.Describe(ex, "certificate"), L10n.Get("btn_Close"));
		}
		finally
		{
			PrepareBtn.IsEnabled = true;
			PrepareBtn.Text = L10n.Get("lblPrepareCertificateNow");
			ShowCertificateStatus();
		}
	}
}
