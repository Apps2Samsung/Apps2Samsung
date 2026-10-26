using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// Settings > Contribute > Add an app: the fields of a community package, checked like the
    /// packages repo checks them, then sent on as a pull request or as the issue form, both in the
    /// browser (see <see cref="AppContribution"/>).
    /// </summary>
    public partial class ContributeAppWindow : Window
    {
        public sealed record Choice(string Id, string Name);

        private readonly Func<string, string> _localize = key => key;

        public string TitleText { get; } = string.Empty;
        public string HintText { get; } = string.Empty;
        public string NameLabel { get; } = string.Empty;
        public string DescriptionLabel { get; } = string.Empty;
        public string CategoryLabel { get; } = string.Empty;
        public string RepoLabel { get; } = string.Empty;
        public string RepoHint { get; } = string.Empty;
        public string SourceLabel { get; } = string.Empty;
        public string BranchLabel { get; } = string.Empty;
        public string UrlLabel { get; } = string.Empty;
        public string ProjectPathLabel { get; } = string.Empty;
        public string OutputNameLabel { get; } = string.Empty;
        public string LicenseLabel { get; } = string.Empty;
        public string AdultLabel { get; } = string.Empty;
        public string NotesLabel { get; } = string.Empty;
        public string PullRequestText { get; } = string.Empty;
        public string IssueText { get; } = string.Empty;
        public string WriteYourselfText { get; } = string.Empty;
        public string CancelText { get; } = string.Empty;

        public ContributeAppWindow()
        {
            InitializeComponent();
        }

        public ContributeAppWindow(Func<string, string> localize) : this()
        {
            _localize = localize;
            var optional = $" ({localize("lblOptional")})";
            TitleText = localize("lblContributeAppTitle");
            HintText = localize("hintContributeAppForm");
            NameLabel = localize("lblContributeName");
            DescriptionLabel = localize("lblContributeDescription");
            CategoryLabel = localize("lblContributeCategory");
            RepoLabel = localize("lblContributeRepo");
            RepoHint = localize("hintContributeRepo");
            SourceLabel = localize("lblContributeSource");
            BranchLabel = localize("lblContributeBranch");
            UrlLabel = localize("lblContributeUrl");
            ProjectPathLabel = localize("lblContributeProjectPath") + optional;
            OutputNameLabel = localize("lblContributeOutputName");
            LicenseLabel = localize("lblContributeLicense");
            AdultLabel = localize("lblContributeAdult");
            NotesLabel = localize("lblContributeNotes") + optional;
            PullRequestText = localize("btnContributePullRequest");
            IssueText = localize("btnContributeIssue");
            WriteYourselfText = localize("lblContributeWriteYourself") + " ↗";
            CancelText = localize("lblCancel");
            DataContext = this;

            CategoryBox.ItemsSource = AppCategories.Ordered.Select(id => new Choice(id, AppCategories.DisplayName(id, localize))).ToList();
            CategoryBox.SelectedIndex = AppCategories.Ordered.Count - 1;
            SourceBox.ItemsSource = AppContribution.Sources.Select(id => new Choice(id, localize(SourceKey(id)))).ToList();
            SourceBox.SelectedIndex = 0;

            RepoBox.Watermark = AppContribution.Examples.Repo;
            BranchBox.Watermark = AppContribution.Examples.Branch;
            UrlBox.Watermark = AppContribution.Examples.Url;
            ProjectPathBox.Watermark = AppContribution.Examples.ProjectPath;
            OutputNameBox.Watermark = AppContribution.Examples.OutputName;
            LicenseBox.Watermark = AppContribution.Examples.License;

            Opened += (_, _) => NameBox.Focus();
        }

        private static string SourceKey(string source) => source switch
        {
            AppContribution.SourceBuild => "lblContributeSourceBuild",
            AppContribution.SourceDirect => "lblContributeSourceDirect",
            _ => "lblContributeSourceRelease",
        };

        private AppContribution Read() => new()
        {
            Name = NameBox.Text ?? string.Empty,
            Description = DescriptionBox.Text ?? string.Empty,
            Category = (CategoryBox.SelectedItem as Choice)?.Id ?? AppCategories.Other,
            Repo = RepoBox.Text ?? string.Empty,
            Source = (SourceBox.SelectedItem as Choice)?.Id ?? AppContribution.SourceRelease,
            Branch = BranchBox.Text ?? string.Empty,
            Url = UrlBox.Text ?? string.Empty,
            ProjectPath = ProjectPathBox.Text ?? string.Empty,
            OutputName = OutputNameBox.Text ?? string.Empty,
            License = LicenseBox.Text ?? string.Empty,
            Adult = AdultBox.IsChecked == true,
            Notes = NotesBox.Text ?? string.Empty,
        };

        // Only the fields the chosen source uses.
        private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
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
            var fields = new (string Key, TextBox Box, TextBlock Error)[]
            {
                ("name", NameBox, NameError),
                ("description", DescriptionBox, DescriptionError),
                ("repo", RepoBox, RepoError),
                ("branch", BranchBox, BranchError),
                ("url", UrlBox, UrlError),
                ("output_name", OutputNameBox, OutputNameError),
                ("license", LicenseBox, LicenseError),
            };
            TextBox? firstBad = null;
            foreach (var (key, box, error) in fields)
            {
                var bad = errors.TryGetValue(key, out var message);
                error.Text = bad ? _localize(message!) : string.Empty;
                error.IsVisible = bad;
                if (bad)
                    firstBad ??= box;
            }
            firstBad?.Focus();
            return firstBad is null ? app : null;
        }

        private void OnPullRequestClick(object? sender, RoutedEventArgs e) => Send(app => app.PullRequestUrl);

        private void OnIssueClick(object? sender, RoutedEventArgs e) => Send(app => app.IssueUrl);

        private void Send(Func<AppContribution, string> link)
        {
            if (Checked() is { } app && Open(link(app)))
                Close();
        }

        private void OnWriteYourselfClick(object? sender, RoutedEventArgs e)
        {
            if (Open(Constants.Links.PackagesReadme))
                Close();
        }

        private static bool Open(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Contribute] Couldn't open GitHub: {ex.Message}");
                return false;
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    }
}
