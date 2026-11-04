using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The certificate sheet. <see cref="Services.CertificateFlow"/> pushes it once provisioning knows a
/// Samsung sign-in is needed, awaits <see cref="SignInChosen"/>, then moves it on with
/// <see cref="SetStage"/> and closes it. Main thread only.
/// </summary>
public partial class CertificateSheetPage : ContentPage
{
	private readonly TaskCompletionSource<bool> _signIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Dictionary<CertificateStage, int> _index = new();
	private readonly string? _tvDuid;

	public CertificateSheetPage(string tvName, CertificatePrivilegeLevel level, string? tvDuid = null)
	{
		InitializeComponent();
		_tvDuid = tvDuid;
		SubLabel.Text = string.Format(L10n.Get("certSheetFor"), tvName);
		LevelLabel.Text = L10n.Get(CertificateStages.LevelKey(level));

		foreach (var (stage, key) in CertificateStages.Steps)
			_index[stage] = Steps.Add(L10n.Get(key));

		// The device ID is read before anyone knows a sign-in is needed, so it is done by now.
		Steps.SetDetail(_index[CertificateStage.SignIn], L10n.Get("certStageSignInHintApp"));
		Steps.SetCurrent(_index[CertificateStage.SignIn]);
	}

	/// <summary>
	/// The Tizen certificate sheet (#988): two steps, Create the certificate instead of a sign-in,
	/// and Use a Samsung certificate instead as the way out. <paramref name="tvName"/> null means
	/// Settings made it with no TV in mind.
	/// </summary>
	public CertificateSheetPage(string? tvName, TizenDistributorLevel level)
	{
		InitializeComponent();
		IsTizen = true;
		SubLabel.Text = tvName is null ? L10n.Get("certSheetForAnyTv") : string.Format(L10n.Get("certSheetFor"), tvName);
		LevelLabel.Text = L10n.Get(CertificateStages.TizenLevelKey(level));
		IntroLabel.Text = L10n.Get("certSheetIntroTizen");
		PrimaryBtn.Text = L10n.Get("lblCreateCertificate");
		ImportHint.Text = L10n.Get("certSheetSamsungInsteadHint");
		ImportBtn.Text = L10n.Get("lblUseSamsungInstead");

		foreach (var (stage, key) in CertificateStages.TizenSteps)
			_index[stage] = Steps.Add(L10n.Get(key));
		Steps.SetDetail(_index[CertificateStage.Create], L10n.Get("certStageCreateHintTizen"));
		Steps.SetDetail(_index[CertificateStage.Save], L10n.Get("certStageSaveHintTizen"));
		Steps.SetCurrent(_index[CertificateStage.Create]);
	}

	/// <summary>The Tizen sheet (#988): no sign-in, the certificate is made right here.</summary>
	public bool IsTizen { get; }

	/// <summary>
	/// True when the user chose a Samsung certificate on the Tizen sheet (#988): instead of making
	/// the Tizen one, or after the TV refused it. <see cref="SignInChosen"/> is true as well then.
	/// </summary>
	public bool SamsungInstead { get; private set; }

	/// <summary>
	/// The TV refused the Tizen certificate (#988): both steps ticked (it was made and kept), the
	/// TV's words in the warning, and Sign in with Samsung instead as the primary button.
	/// <see cref="SignInChosen"/> then says whether to switch.
	/// </summary>
	public void ShowRefused(string profileName, string? reason, DateTime? validUntil)
	{
		Steps.SetDetail(_index[CertificateStage.Create], validUntil is { } until ? $"{profileName}, {until:D}" : profileName);
		Steps.SetDetail(_index[CertificateStage.Save], L10n.Get("certStageSaveDoneTizen"));
		Steps.SetCurrent(Steps.Count);
		RefusedBody.Text = string.Format(L10n.Get("certTizenRefusedBody"), reason ?? "[118, -12]");
		IntroLabel.IsVisible = false;
		RefusedPanel.IsVisible = true;
		SignInButtons.IsVisible = false;
		RefusedButtons.IsVisible = true;
	}

	/// <summary>True once the user chose Sign in with Samsung; false on Cancel or when the sheet is
	/// swiped away first.</summary>
	public Task<bool> SignInChosen => _signIn.Task;

	/// <summary>Provisioning reached <paramref name="stage"/>; <see cref="CertificateStage.Done"/>
	/// ticks every step.</summary>
	public void SetStage(CertificateStage stage)
	{
		var index = stage == CertificateStage.Done ? Steps.Count : _index[stage];
		if (!IsTizen && index > _index[CertificateStage.SignIn])
			Steps.SetDetail(_index[CertificateStage.SignIn], null);
		Steps.SetCurrent(index);
	}

	/// <summary>The current step's detail line (a progress message from the certificate service).</summary>
	public void Report(string message)
	{
		if (Steps.Current < Steps.Count)
			Steps.SetDetail(Steps.Current, message);
	}

	/// <summary>Making the certificate failed: the current step turns red, and Close appears.</summary>
	public void Fail(string message)
	{
		Steps.Fail(message);
		SignInButtons.IsVisible = false;
		CloseBtn.IsVisible = true;
	}

	/// <summary>Closes the sheet if it is still the page on top.</summary>
	public async Task CloseAsync()
	{
		if (ReferenceEquals(Navigation.ModalStack.LastOrDefault(), this))
			await Navigation.PopModalAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_signIn.TrySetResult(false);
	}

	private void OnSignInClicked(object? sender, EventArgs e)
	{
		SignInButtons.IsVisible = false;
		_signIn.TrySetResult(true);
	}

	// Import a certificate exported on another device instead of signing in (#921): pick the file,
	// show what it holds and what it replaces, write it. Covering this TV, provisioning resumes as
	// if Sign in had been chosen and finds the profile on disk; otherwise the author certificate is
	// now here and Sign in only extends it to this TV.
	// Use a Samsung certificate instead / Sign in with Samsung instead (#988): the flow switches the
	// setting and runs the Samsung path, with its own sheet.
	private async void OnSamsungInsteadClicked(object? sender, EventArgs e)
	{
		SamsungInstead = true;
		_signIn.TrySetResult(true);
		await CloseAsync();
	}

	private async void OnImportClicked(object? sender, EventArgs e)
	{
		if (IsTizen)
		{
			OnSamsungInsteadClicked(sender, e);
			return;
		}
		try
		{
			var outcome = await CertificateArchiveFiles.PickAndImportAsync(this, _tvDuid);
			if (outcome is null)
				return;

			if (outcome.CoversTv)
			{
				SignInButtons.IsVisible = false;
				Steps.SetDetail(_index[CertificateStage.SignIn], string.Format(L10n.Get("statusCertificateImported"), outcome.FileCount));
				_signIn.TrySetResult(true);
			}
			else
			{
				Steps.SetDetail(_index[CertificateStage.SignIn], L10n.Get("certArchiveImportedNotCovering"));
				ImportBtn.IsVisible = false;
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("certArchiveImportTitle"),
				string.Format(L10n.Get("statusCertificateImportFailed"), ErrorText.Describe(ex, "certificate/import")),
				L10n.Get("lblOk"));
		}
	}

	private async void OnCancelClicked(object? sender, EventArgs e)
	{
		_signIn.TrySetResult(false);
		await CloseAsync();
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();
}
