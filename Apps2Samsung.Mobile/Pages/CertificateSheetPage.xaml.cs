using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;

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

	public CertificateSheetPage(string tvName, CertificatePrivilegeLevel level)
	{
		InitializeComponent();
		SubLabel.Text = string.Format(L10n.Get("certSheetFor"), tvName);
		LevelLabel.Text = L10n.Get(CertificateStages.LevelKey(level));

		foreach (var (stage, key) in CertificateStages.Steps)
			_index[stage] = Steps.Add(L10n.Get(key));

		// The device ID is read before anyone knows a sign-in is needed, so it is done by now.
		Steps.SetDetail(_index[CertificateStage.SignIn], L10n.Get("certStageSignInHintApp"));
		Steps.SetCurrent(_index[CertificateStage.SignIn]);
	}

	/// <summary>True once the user chose Sign in with Samsung; false on Cancel or when the sheet is
	/// swiped away first.</summary>
	public Task<bool> SignInChosen => _signIn.Task;

	/// <summary>Provisioning reached <paramref name="stage"/>; <see cref="CertificateStage.Done"/>
	/// ticks every step.</summary>
	public void SetStage(CertificateStage stage)
	{
		var index = stage == CertificateStage.Done ? Steps.Count : _index[stage];
		if (index > _index[CertificateStage.SignIn])
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

	private async void OnCancelClicked(object? sender, EventArgs e)
	{
		_signIn.TrySetResult(false);
		await CloseAsync();
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();
}
