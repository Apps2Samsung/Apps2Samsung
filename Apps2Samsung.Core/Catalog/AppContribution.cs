using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// An app a user wants in the community catalog, filled in from Settings > Contribute > Add an
    /// app. It goes to tizen-community-packages one of two ways, both in the browser so the app
    /// holds no GitHub login: <see cref="PullRequestUrl"/> opens GitHub's new-file page with
    /// <c>packages/&lt;owner&gt;__&lt;repo&gt;.json</c> already written (GitHub forks the repo and
    /// the user proposes the change as a pull request), <see cref="IssueUrl"/> opens the
    /// "Contribute an App" form pre-filled for a maintainer to add it.
    /// </summary>
    public sealed class AppContribution
    {
        public const string PackagesRepo = "Apps2Samsung/tizen-community-packages";

        public const string SourceRelease = "release";
        public const string SourceBuild = "build";
        public const string SourceDirect = "direct";
        public static readonly IReadOnlyList<string> Sources = new[] { SourceRelease, SourceBuild, SourceDirect };

        // The checks packages.schema.json makes, so a pull request from here passes the PR bot.
        private static readonly Regex RepoPattern = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
        private static readonly Regex OutputNamePattern = new(@"\.(wgt|tpk)$", RegexOptions.Compiled);

        /// <summary>Sample values for the empty fields: technical, the same in every language.</summary>
        public static class Examples
        {
            public const string Repo = "yourname/your-app";
            public const string Branch = "main";
            public const string Url = "https://example.com/MyApp.wgt";
            public const string ProjectPath = ".";
            public const string OutputName = "MyApp.wgt";
            public const string License = "MIT";
        }

        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = AppCategories.Other;
        /// <summary>owner/repo on GitHub; a URL to the repository is accepted and trimmed to that.</summary>
        public string Repo { get; set; } = string.Empty;
        public string Source { get; set; } = SourceRelease;
        /// <summary>release and build only.</summary>
        public string Branch { get; set; } = string.Empty;
        public string OutputName { get; set; } = string.Empty;
        /// <summary>direct only.</summary>
        public string Url { get; set; } = string.Empty;
        /// <summary>build only; blank means the repository root.</summary>
        public string ProjectPath { get; set; } = string.Empty;
        /// <summary>Not in the package file; the issue form asks for it.</summary>
        public string License { get; set; } = string.Empty;
        public bool Adult { get; set; }
        public string Notes { get; set; } = string.Empty;

        public bool UsesBranch => Source is SourceRelease or SourceBuild;
        public bool UsesUrl => Source == SourceDirect;
        public bool UsesProjectPath => Source == SourceBuild;

        /// <summary><see cref="Repo"/> as owner/repo, from either that or a github.com URL.</summary>
        public string RepoSlug
        {
            get
            {
                var repo = Repo.Trim();
                var marker = repo.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
                if (marker >= 0)
                    repo = repo[(marker + "github.com/".Length)..];
                var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return repo.Trim('/');
                var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
                return $"{parts[0]}/{name}";
            }
        }

        /// <summary>packages/&lt;owner&gt;__&lt;repo&gt;.json, as packages/README.md names them.</summary>
        public string FileName => RepoSlug.Replace("/", "__") + ".json";

        /// <summary>
        /// What is wrong, by field (name, description, repo, branch, output_name, url, license) to
        /// the en.json key of the message; empty when it can be sent.
        /// </summary>
        public IReadOnlyDictionary<string, string> Validate()
        {
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            CheckLine("name", Name);
            CheckLine("description", Description);
            if (!RepoPattern.IsMatch(RepoSlug))
                errors["repo"] = "errContributeRepo";
            if (UsesBranch && string.IsNullOrWhiteSpace(Branch))
                errors["branch"] = "errContributeRequired";
            if (string.IsNullOrWhiteSpace(OutputName))
                errors["output_name"] = "errContributeRequired";
            else if (!OutputNamePattern.IsMatch(OutputName.Trim()))
                errors["output_name"] = "errContributeOutputName";
            if (UsesUrl && !(Url.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                             || Url.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                errors["url"] = "errContributeUrl";
            if (string.IsNullOrWhiteSpace(License))
                errors["license"] = "errContributeRequired";
            return errors;

            void CheckLine(string field, string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    errors[field] = "errContributeRequired";
                else if (value.Contains('|') || value.Contains('\n'))
                    errors[field] = "errContributeOneLine";
            }
        }

        /// <summary>The package file, in the field order and 2-space indent of the files in packages/.</summary>
        public string ToPackageJson()
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                // The files in packages/ use LF; the default would be CRLF on Windows.
                NewLine = "\n",
                // Keep é, ü and the like as they are, like the hand-written files do.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();
                writer.WriteString("name", Name.Trim());
                writer.WriteString("description", Description.Trim());
                writer.WriteString("category", AppCategories.Normalize(Category));
                writer.WriteString("repo", RepoSlug);
                if (Adult)
                    writer.WriteBoolean("adult", true);
                writer.WriteString("source", Source);
                if (UsesBranch)
                    writer.WriteString("branch", Branch.Trim());
                if (UsesProjectPath && !string.IsNullOrWhiteSpace(ProjectPath))
                    writer.WriteString("project_path", ProjectPath.Trim());
                if (UsesUrl)
                    writer.WriteString("url", Url.Trim());
                writer.WriteString("output_name", OutputName.Trim());
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        }

        /// <summary>
        /// GitHub's "create new file" page in the packages repo with the file name and contents
        /// filled in. For someone without write access GitHub forks the repo first, and the
        /// "Propose changes" button leads straight to the pull request.
        /// </summary>
        public string PullRequestUrl
            => $"https://github.com/{PackagesRepo}/new/main/packages"
               + $"?filename={Uri.EscapeDataString(FileName)}"
               + $"&value={Uri.EscapeDataString(ToPackageJson())}"
               + $"&message={Uri.EscapeDataString($"Add {Name.Trim()}")}";

        /// <summary>
        /// The "Contribute an App" issue form, each field pre-filled by its id. The form has no
        /// category or 18+ question of its own, so those go first in the notes; the confirmations
        /// are ticked on GitHub.
        /// </summary>
        public string IssueUrl
        {
            get
            {
                var notes = $"Category: {AppCategories.Normalize(Category)}" + (Adult ? "\nAdults only (18+)" : string.Empty);
                if (!string.IsNullOrWhiteSpace(Notes))
                    notes += "\n\n" + Notes.Trim();
                var fields = new List<(string Key, string Value)>
                {
                    ("template", "contribute-app.yml"),
                    ("title", $"[Contribution] {Name.Trim()}"),
                    ("name", Name.Trim()),
                    ("description", Description.Trim()),
                    ("repo", RepoSlug),
                    ("source", Source),
                    ("output_name", OutputName.Trim()),
                    ("branch", UsesBranch ? Branch.Trim() : string.Empty),
                    ("url", UsesUrl ? Url.Trim() : string.Empty),
                    ("project_path", UsesProjectPath ? ProjectPath.Trim() : string.Empty),
                    ("license", License.Trim()),
                    ("notes", notes),
                };
                return $"https://github.com/{PackagesRepo}/issues/new?"
                       + string.Join("&", fields.Where(f => f.Value.Length > 0)
                           .Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value)}"));
            }
        }
    }
}
