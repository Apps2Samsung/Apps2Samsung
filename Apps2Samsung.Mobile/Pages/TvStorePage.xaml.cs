using Apps2Samsung.Catalog;
using Apps2Samsung.Certificate;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Localization;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Controls;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.TvStore;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The TV-Store tab (#1041): the catalog on the TV itself, out of the toolbox. The setup check reads
/// the TV without changing anything (TV-Store installed and its version, what Host PC IP means for
/// it); then what the phone does with TV-Store: hand it the certificate (Arm, #844), send it signed
/// repositories (#1018) and send it a package (#979). Follows the shared TV selection like On the TV.
/// </summary>
public partial class TvStorePage : ContentPage
{
	private readonly ISdbEngine _sdb;
	private readonly TvSelection _tv;
	private readonly CertificateFlow _certFlow;
	private readonly WgtInstaller _installer;

	// The TV the check last ran for, so coming back to the tab doesn't read the TV again.
	private string? _checkedFor;
	private string _tvIp = string.Empty;
	private string _tvLabel = string.Empty;

	// One action at a time: Arm, Send sources and Send a package all talk to TV-Store.
	private bool _busy;
	private bool _checking;

	// TV-Store's catalog entry and whether the TV runs an old one, for Show in catalog (#888).
	private GitHubRelease? _tvStoreEntry;
	private bool _tvStoreOutdated;

	public TvStorePage(ISdbEngine sdb, TvSelection tv, CertificateFlow certFlow, WgtInstaller installer)
	{
		InitializeComponent();
		_sdb = sdb;
		_tv = tv;
		_certFlow = certFlow;
		_installer = installer;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		_tv.Changed -= OnTvChanged;
		_tv.Changed += OnTvChanged;
		ShowTvSources();
		await ShowSelectedTvAsync(force: false);
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_tv.Changed -= OnTvChanged;
	}

	private async void OnTvChanged()
	{
		if (_tv.Ip != _checkedFor || !_tv.HasTv)
			await ShowSelectedTvAsync(force: false);
	}

	private async void OnChangeTvClicked(object? sender, EventArgs e) => await TvPicker.ChooseAsync(this, _tv);

	private async void OnCheckAgainClicked(object? sender, EventArgs e) => await ShowSelectedTvAsync(force: true);

	private async Task ShowSelectedTvAsync(bool force)
	{
		TopBar.Title = _tv.HasTv ? _tv.DisplayName : L10n.Get("lblNoTvFound");
		TopBar.TvCount = _tv.Tvs.Count;
		ArmBtn.IsEnabled = _tv.HasTv;
		SendSourcesBtn.IsEnabled = _tv.HasTv;
		SendPackageBtn.IsEnabled = _tv.HasTv;
		if (!_tv.HasTv)
		{
			_checkedFor = null;
			ChecksHost.Clear();
			ShowCard("TV-Store", L10n.Get("statusSelectTvFirst"), "Muted");
			TopBar.Subtitle = L10n.Get("statusSelectTvFirst");
			TopBar.Status = TopBarStatus.Warn;
			return;
		}
		var changed = _tv.Ip != _checkedFor;
		_tvIp = _tv.Ip!;
		_tvLabel = _tv.Label;
		if (changed)
		{
			ArmStatusLabel.Text = string.Empty;
			SourcesStatusLabel.Text = string.Empty;
			PackageStatusLabel.Text = string.Empty;
		}
		if (force || changed)
			await CheckAsync();
	}

	/// <summary>The setup check: reads the TV (app list, REST version, Host PC IP), changes nothing.</summary>
	private async Task CheckAsync()
	{
		if (_checking)
			return;
		_checking = true;
		var tvIp = _tvIp;
		_checkedFor = tvIp;
		ShowCard("TV-Store", L10n.Get("lblTvStoreChecking"), "Accent");
		TopBar.Subtitle = L10n.Get("lblTvStoreChecking");
		TopBar.Status = TopBarStatus.Busy;
		try
		{
			var overview = await TvStoreSetupCheck.ReadAsync(tvIp, _sdb, MobileSettings.PackageIdRenamesJson);
			if (tvIp != _tvIp)
				return;
			var lines = TvStoreSetupCheck.Lines(overview, tvIp, Apps2Samsung.Sdb.SdbEndpoint.LocalAddresses());

			ChecksHost.Clear();
			foreach (var line in lines)
				ChecksHost.Add(CheckRow(line));

			var worst = lines.Any(l => l.Level == TvStoreCheckLevel.Bad) ? TvStoreCheckLevel.Bad
				: lines.Any(l => l.Level == TvStoreCheckLevel.Warn) ? TvStoreCheckLevel.Warn
				: lines.Any(l => l.Level == TvStoreCheckLevel.Unknown) ? TvStoreCheckLevel.Unknown
				: TvStoreCheckLevel.Ok;
			var first = lines.Count > 0 ? Text(lines[0]) : string.Empty;
			ShowCard(overview.Version is null ? "TV-Store" : $"TV-Store {overview.Version}", first, Token(worst));
			TopBar.Subtitle = first;
			TopBar.Status = worst switch
			{
				TvStoreCheckLevel.Ok => TopBarStatus.Ok,
				TvStoreCheckLevel.Warn => TopBarStatus.Warn,
				TvStoreCheckLevel.Bad => TopBarStatus.Bad,
				_ => TopBarStatus.None,
			};

			_tvStoreEntry = TvStorePackage.FindInCatalog(CatalogStore.Releases);
			_tvStoreOutdated = overview.State == TvStoreInstallState.Outdated;
			ShowInCatalogBtn.IsVisible = _tvStoreEntry is not null &&
				overview.State is TvStoreInstallState.NotInstalled or TvStoreInstallState.Outdated;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[tvstore] setup check failed: {ex}");
			ShowCard("TV-Store", string.Format(L10n.Get("lblToolboxTvStoreFailed"), ex.Message), "Muted");
			TopBar.Status = TopBarStatus.None;
		}
		finally
		{
			_checking = false;
		}
	}

	private void ShowCard(string headline, string subline, string ledToken)
	{
		HeadlineLabel.Text = headline;
		SublineLabel.Text = subline;
		Led.SetDynamicResource(Shape.FillProperty, ledToken + "Brush");
		NeedsSdbHint.IsVisible = false;
	}

	private static string Token(TvStoreCheckLevel level) => level switch
	{
		TvStoreCheckLevel.Ok => "Ok",
		TvStoreCheckLevel.Warn => "Warn",
		TvStoreCheckLevel.Bad => "Bad",
		_ => "Muted",
	};

	private static string Text(TvStoreCheck check) =>
		check.Args.Length == 0 ? L10n.Get(check.Key) : string.Format(L10n.Get(check.Key), check.Args);

	// One line of the check: its mark in the status colour, then the sentence.
	private static View CheckRow(TvStoreCheck check)
	{
		var mark = new Label
		{
			Text = check.Level switch
			{
				TvStoreCheckLevel.Ok => "✓",
				TvStoreCheckLevel.Warn => "!",
				TvStoreCheckLevel.Bad => "✕",
				_ => "?",
			},
			FontFamily = "OpenSansSemibold",
			FontSize = 15,
			WidthRequest = 16,
			VerticalOptions = LayoutOptions.Start,
		};
		mark.SetDynamicResource(Label.TextColorProperty, Token(check.Level) + "Color");
		var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8 };
		row.Add(mark, 0, 0);
		row.Add(new Label { Text = Text(check), FontSize = 14, LineBreakMode = LineBreakMode.WordWrap }, 1, 0);
		return row;
	}

	// Leaves for the Install tab and opens TV-Store's sheet there, as an Update when the set has an old one.
	private async void OnShowInCatalogClicked(object? sender, EventArgs e)
	{
		if (_tvStoreEntry is null || _busy)
			return;
		await CatalogStore.OpenOnInstallTabAsync(new CatalogOpenRequest(_tvStoreEntry, null, _tvStoreOutdated));
	}

	/// <summary>
	/// Hands the signing certificate to the TV-Store app on the TV (#844, phase 3): the profile this
	/// head installs with, as PEM, over TV-Store's page in debug mode. Once armed, the TV signs and
	/// installs catalog apps on its own. TV-Store has to be on the set already.
	/// </summary>
	private async void OnArmClicked(object? sender, EventArgs e)
	{
		if (_busy || !_tv.HasTv)
			return;

		_busy = true;
		ArmBtn.IsEnabled = false;
		var tvIp = _tvIp;
		try
		{
			// This device's package-id renames (#839): a TV-Store installed under a new id is TV-Store too.
			var renames = MobileSettings.PackageIdRenamesJson;
			var state = await TvStorePackage.GetInstallStateAsync(_sdb, tvIp, renames);
			if (state != TvStoreInstallState.Installed)
			{
				SetArmStatus(L10n.Get(state == TvStoreInstallState.Outdated ? "lblToolboxTvStoreOutdated" : "lblToolboxTvStoreNotInstalled"));
				// The message stays; one more button takes the user to TV-Store's catalog sheet (#888).
				_tvStoreEntry = TvStorePackage.FindInCatalog(CatalogStore.Releases);
				_tvStoreOutdated = state == TvStoreInstallState.Outdated;
				ShowInCatalogBtn.IsVisible = _tvStoreEntry is not null;
				return;
			}

			SetArmStatus(L10n.Get("lblToolboxTvStoreCertificate"));
			var signingProfile = TvToolHooks.SigningProfileFor(_certFlow, _sdb, Navigation, tvIp, _tvLabel);
			var profile = await signingProfile(false, message => MainThread.BeginInvokeOnMainThread(() => SetArmStatus(message)));
			if (profile is null)
			{
				SetArmStatus(L10n.Get("lblToolboxTvStoreNoCertificate"));
				return;
			}
			// Every profile on this phone goes along, the covering one first (#44), so TV-Store can
			// update an app whichever of them installed it. The store is the profile's parent folder.
			var pems = SigningCertificatePem.OpenAll(profile, SigningCertificatePem.ProfilesIn(System.IO.Path.GetDirectoryName(profile.ProfileDir)));

			var progress = new Progress<string>(key => SetArmStatus(L10n.Get(key)));
			await using var store = await TvStoreClient.AttachAsync(_sdb, tvIp, progress, renames);
			SetArmStatus(L10n.Get("lblToolboxTvStoreArming"));
			await store.ArmAsync(pems);
			var status = await store.StatusAsync();
			// What is left to do depends on the Host PC IP the TV has right now (#960).
			var tv = await TizenDeveloperInfo.ReadAsync(new NetworkDevice { IpAddress = tvIp });
			var next = TvStoreNextSteps.For(tv.DeveloperIP, status.OwnIp);
			SetArmStatus(string.Format(L10n.Get(TvStoreNextSteps.MessageKey(next)), store.Version, status.OwnIp ?? tvIp));
		}
		catch (OperationCanceledException)
		{
			// The certificate sheet was dismissed: nothing to hand over.
			SetArmStatus(L10n.Get("lblToolboxTvStoreNoCertificate"));
		}
		catch (TvStoreUnreachableException ex)
		{
			// The listing failed, so TV-Store may well be there: point at Developer Mode, not the catalog (#889).
			System.Diagnostics.Trace.WriteLine($"[tvstore] arm: no app listing: {ex.Reason}");
			SetArmStatus(string.Format(L10n.Get("lblToolboxTvStoreUnreachable"), ex.Reason));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[tvstore] arm failed: {ex}");
			SetArmStatus(string.Format(L10n.Get("lblToolboxTvStoreFailed"), ex.Message));
		}
		finally
		{
			_busy = false;
			ArmBtn.IsEnabled = _tv.HasTv;
		}
	}

	private void SetArmStatus(string message) => ArmStatusLabel.Text = message;

	// ---- Send sources to TV (#1018, TV-Store#82) ----

	/// <summary>One signed repository in the list: ticked to go to the TV unless its key changed since (review it first).</summary>
	private sealed record TvSourceRow(UserSource Source, bool CanSend, CheckBox Check, Label Note);

	private readonly List<TvSourceRow> _tvSources = new();

	// The approved signed repositories of Your sources, ticked; one whose key changed is listed off with a note.
	private void ShowTvSources()
	{
		TvSourceRows.Clear();
		_tvSources.Clear();
		foreach (var source in UserSources.Parse(MobileSettings.UserSourcesJson).Where(UserSources.IsApprovedRepository))
		{
			var keyChanged = Apps2Samsung.Catalog.Repositories.SignedRepositories.LastLoad(source.Url)?.State
				== Apps2Samsung.Catalog.Repositories.RepositoryState.KeyChanged;
			var check = new CheckBox { IsChecked = !keyChanged, IsEnabled = !keyChanged, VerticalOptions = LayoutOptions.Center };
			var note = new Label
			{
				Text = keyChanged ? L10n.Get("lblToolboxSendSourcesReview") : string.Empty, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap,
			};
			note.SetDynamicResource(Label.TextColorProperty, "MutedColor");
			note.IsVisible = !string.IsNullOrEmpty(note.Text);
			var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 4 };
			row.Add(check, 0, 0);
			row.Add(new VerticalStackLayout
			{
				Spacing = 1, VerticalOptions = LayoutOptions.Center,
				Children = { new Label { Text = UserSources.DisplayName(source), LineBreakMode = LineBreakMode.TailTruncation }, note },
			}, 1, 0);
			TvSourceRows.Add(row);
			_tvSources.Add(new TvSourceRow(source, !keyChanged, check, note));
		}
		NoTvSourcesLabel.IsVisible = _tvSources.Count == 0;
		TvSourceRows.IsVisible = SendSourcesBtn.IsVisible = _tvSources.Count > 0;
	}

	/// <summary>
	/// Hands the ticked signed repositories to TV-Store with the keys approved here; the TV asks the
	/// person at it for each, and this waits for the answers (up to five minutes).
	/// </summary>
	private async void OnSendSourcesClicked(object? sender, EventArgs e)
	{
		var chosen = _tvSources.Where(r => r.CanSend && r.Check.IsChecked).Select(r => r.Source).ToList();
		if (chosen.Count == 0)
		{
			SourcesStatusLabel.Text = L10n.Get("lblToolboxSendSourcesNone");
			return;
		}
		if (_busy || !_tv.HasTv)
			return;

		_busy = true;
		SendSourcesBtn.IsEnabled = false;
		try
		{
			var renames = MobileSettings.PackageIdRenamesJson;
			var progress = new Progress<string>(key => SourcesStatusLabel.Text = L10n.Get(key));
			await using var store = await TvStoreClient.AttachAsync(_sdb, _tvIp, progress, renames);
			SourcesStatusLabel.Text = L10n.Get("lblToolboxSendSourcesWaiting");
			var results = await store.AddSourcesAsync(chosen);
			foreach (var result in results)
			{
				var row = _tvSources.FirstOrDefault(r => string.Equals(r.Source.RepositoryId, result.RepositoryId, StringComparison.Ordinal));
				if (row is null)
					continue;
				row.Note.Text = result.Added ? L10n.Get("lblSourceOnTv") : result.Error ?? string.Empty;
				row.Note.IsVisible = !string.IsNullOrEmpty(row.Note.Text);
			}
			var lines = new List<string> { string.Format(L10n.Get("lblToolboxSendSourcesDone"), results.Count(r => r.Added), results.Count) };
			lines.AddRange(results.Where(r => !r.Added).Select(r => string.Format(L10n.Get("lblToolboxSendSourcesNotAdded"), r.Name ?? r.RepositoryId, r.Error)));
			SourcesStatusLabel.Text = string.Join(Environment.NewLine, lines);
		}
		catch (TvStoreNotInstalledException ex)
		{
			SourcesStatusLabel.Text = L10n.Get(ex.IsOutdated ? "lblToolboxTvStoreOutdated" : "lblToolboxTvStoreNotInstalled");
		}
		catch (TvStoreUnreachableException ex)
		{
			SourcesStatusLabel.Text = string.Format(L10n.Get("lblToolboxTvStoreUnreachable"), ex.Reason);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[tvstore] send sources failed: {ex}");
			// Too old a TV-Store says so in its own words.
			SourcesStatusLabel.Text = ex is NotSupportedException ? ex.Message : string.Format(L10n.Get("lblToolboxTvStoreFailed"), ex.Message);
		}
		finally
		{
			_busy = false;
			SendSourcesBtn.IsEnabled = _tv.HasTv;
		}
	}

	/// <summary>
	/// Send a package (#979): a .wgt/.tpk from the phone goes to TV-Store's upload page on the TV,
	/// customized as an install would (the values asked before install) but not signed; TV-Store is
	/// opened on the TV when it is not running, asks on the screen and signs it itself. No developer
	/// connection from this phone is involved, so it works while Host PC IP is 127.0.0.1.
	/// </summary>
	private async void OnSendPackageClicked(object? sender, EventArgs e)
	{
		if (_busy || !_tv.HasTv)
			return;

		var input = await TvToolHooks.PickPackageCopyAsync(message => PackageStatusLabel.Text = message);
		if (input is null)
			return;

		_busy = true;
		SendPackageBtn.IsEnabled = false;
		var tvIp = _tvIp;
		try
		{
			Action<string> report = line => MainThread.BeginInvokeOnMainThread(() => PackageStatusLabel.Text = ProgressText.Localize(line, L10n.Get));
			if (!await TvStoreHandoff.EnsureRunningAsync(tvIp, report))
			{
				PackageStatusLabel.Text = L10n.Get("tvStoreNotRunning");
				return;
			}
			// The picked file is the cache copy, so the customization may edit it in place.
			await _installer.CustomizeAsync(tvIp, input, report);
			var sent = await TvStoreHandoff.SendAsync(tvIp, input, report);
			PackageStatusLabel.Text = string.Format(L10n.Get(sent.TvStoreOpened ? "tvStoreSentBody" : "tvStoreSentBodyClosed"), sent.Upload.Name);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[tvstore] send package failed: {ex}");
			PackageStatusLabel.Text = string.Format(L10n.Get("lblToolboxTvStoreFailed"), ex.Message);
		}
		finally
		{
			try { File.Delete(input); } catch { /* the cache copy; best-effort */ }
			_busy = false;
			SendPackageBtn.IsEnabled = _tv.HasTv;
		}
	}
}
