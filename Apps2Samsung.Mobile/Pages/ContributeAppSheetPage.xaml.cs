using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Controls;
using Apps2Samsung.Mobile.Localization;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Settings > Contribute > Add an app: the fields of a community package, checked like the
/// packages repo checks them, then sent on as a pull request or as the issue form, both in the
/// browser (see <see cref="AppContribution"/>).
/// </summary>
public partial class ContributeAppSheetPage : ContentPage
{
	public ContributeAppSheetPage()
	{
		InitializeComponent();
		var optional = $" ({L10n.Get("lblOptional")})";
		ProjectPathLabel.Text = L10n.Get("lblContributeProjectPath") + optional;
		NotesLabel.Text = L10n.Get("lblContributeNotes") + optional;

		foreach (var id in AppCategories.Ordered)
			CategoryPicker.Items.Add(AppCategories.DisplayName(id, L10n.Get));
		CategoryPicker.SelectedIndex = AppCategories.Ordered.Count - 1;
		foreach (var source in AppContribution.Sources)
			SourcePicker.Items.Add(L10n.Get(SourceKey(source)));
		SourcePicker.SelectedIndex = 0;

		RepoEntry.Placeholder = AppContribution.Examples.Repo;
		BranchEntry.Placeholder = AppContribution.Examples.Branch;
		UrlEntry.Placeholder = AppContribution.Examples.Url;
		ProjectPathEntry.Placeholder = AppContribution.Examples.ProjectPath;
		OutputNameEntry.Placeholder = AppContribution.Examples.OutputName;
		LicenseEntry.Placeholder = AppContribution.Examples.License;
	}

	private static string SourceKey(string source) => source switch
	{
		AppContribution.SourceBuild => "lblContributeSourceBuild",
		AppContribution.SourceDirect => "lblContributeSourceDirect",
		_ => "lblContributeSourceRelease",
	};

	private AppContribution Read() => new()
	{
		Name = NameEntry.Text ?? string.Empty,
		Description = DescriptionEntry.Text ?? string.Empty,
		Category = CategoryPicker.SelectedIndex >= 0 ? AppCategories.Ordered[CategoryPicker.SelectedIndex] : AppCategories.Other,
		Repo = RepoEntry.Text ?? string.Empty,
		Source = SourcePicker.SelectedIndex >= 0 ? AppContribution.Sources[SourcePicker.SelectedIndex] : AppContribution.SourceRelease,
		Branch = BranchEntry.Text ?? string.Empty,
		Url = UrlEntry.Text ?? string.Empty,
		ProjectPath = ProjectPathEntry.Text ?? string.Empty,
		OutputName = OutputNameEntry.Text ?? string.Empty,
		License = LicenseEntry.Text ?? string.Empty,
		Adult = AdultSwitch.IsToggled,
		Notes = NotesEditor.Text ?? string.Empty,
	};

	// Only the fields the chosen source uses.
	private void OnSourceChanged(object? sender, EventArgs e)
	{
		var app = Read();
		BranchRow.IsVisible = app.UsesBranch;
		UrlRow.IsVisible = app.UsesUrl;
		ProjectPathRow.IsVisible = app.UsesProjectPath;
	}

	/// <summary>Shows each field's error under it; the contribution when there are none.</summary>
	private AppContribution? Checked()
	{
		var app = Read();
		var errors = app.Validate();
		var fields = new (string Key, Entry Entry, Label Error)[]
		{
			("name", NameEntry, NameError),
			("description", DescriptionEntry, DescriptionError),
			("repo", RepoEntry, RepoError),
			("branch", BranchEntry, BranchError),
			("url", UrlEntry, UrlError),
			("output_name", OutputNameEntry, OutputNameError),
			("license", LicenseEntry, LicenseError),
		};
		Entry? firstBad = null;
		foreach (var (key, entry, error) in fields)
		{
			var bad = errors.TryGetValue(key, out var message);
			error.Text = bad ? L10n.Get(message!) : string.Empty;
			error.IsVisible = bad;
			if (bad)
				firstBad ??= entry;
		}
		firstBad?.Focus();
		return firstBad is null ? app : null;
	}

	private async void OnPullRequestClicked(object? sender, EventArgs e) => await SendAsync(app => app.PullRequestUrl);

	private async void OnIssueClicked(object? sender, EventArgs e) => await SendAsync(app => app.IssueUrl);

	private async Task SendAsync(Func<AppContribution, string> link)
	{
		if (Checked() is not { } app)
			return;
		await LinkOpener.OpenAsync(link(app));
		await Navigation.PopModalAsync();
	}

	private async void OnCancelClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();
}
