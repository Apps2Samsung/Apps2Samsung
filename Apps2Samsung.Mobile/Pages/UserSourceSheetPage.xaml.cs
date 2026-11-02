using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Localization;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Add a source of your own: a GitHub repository or release, or a direct .wgt/.tpk link, checked
/// with <see cref="UserSources.Validate"/> against the sources already there. <see cref="Result"/>
/// completes with the new <see cref="UserSource"/>, or null when the sheet is dismissed.
/// </summary>
public partial class UserSourceSheetPage : ContentPage
{
	private readonly IReadOnlyList<UserSource> _existing;
	private readonly TaskCompletionSource<UserSource?> _result = new();

	public Task<UserSource?> Result => _result.Task;

	public UserSourceSheetPage(IReadOnlyList<UserSource> existing)
	{
		InitializeComponent();
		_existing = existing;
		NameLabel.Text = $"{L10n.Get("lblUserSourceName")} ({L10n.Get("lblOptional")})";
		foreach (var id in AppCategories.Ordered)
			CategoryPicker.Items.Add(AppCategories.DisplayName(id, L10n.Get));
		CategoryPicker.SelectedIndex = AppCategories.Ordered.Count - 1;
		UrlEntry.Placeholder = "github.com/yourname/your-app";
	}

	/// <summary>Opens the sheet and waits for its answer.</summary>
	public static async Task<UserSource?> AskAsync(INavigation navigation, IReadOnlyList<UserSource> existing)
	{
		var sheet = new UserSourceSheetPage(existing);
		await navigation.PushModalAsync(sheet);
		return await sheet.Result;
	}

	private UserSource Read() => new()
	{
		Url = UrlEntry.Text?.Trim() ?? string.Empty,
		Name = NameEntry.Text?.Trim() ?? string.Empty,
		Category = CategoryPicker.SelectedIndex >= 0 ? AppCategories.Ordered[CategoryPicker.SelectedIndex] : AppCategories.Other,
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
		_result.TrySetResult(source);
		await Navigation.PopModalAsync();

		void Show(Label error, string field)
		{
			var bad = errors.TryGetValue(field, out var key);
			error.Text = bad ? L10n.Get(key!) : string.Empty;
			error.IsVisible = bad;
		}
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
