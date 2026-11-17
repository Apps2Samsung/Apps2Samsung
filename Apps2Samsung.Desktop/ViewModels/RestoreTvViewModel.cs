using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Catalog;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.ViewModels
{
    /// <summary>One file an app can be restored as, when the TV could not say which one it had.</summary>
    public sealed record RestoreBuildChoice(Asset Asset, string Label);

    /// <summary>An app the restore can install: ticked or not, with what will be installed.</summary>
    public partial class RestoreTvRow : ObservableObject
    {
        private readonly Func<GitHubRelease, Asset, Task<bool>>? _collectSettings;
        private readonly Action _changed;

        public TvProfileApp App { get; }
        public GitHubRelease Release { get; }
        public string Name => Release.Name;
        public string Initials { get; }
        public string Color { get; }

        /// <summary>The builds to pick from; more than one only when the build on the TV was a guess.</summary>
        public IReadOnlyList<RestoreBuildChoice> Builds { get; }
        public bool PickBuild => Builds.Count > 1;

        [ObservableProperty] private RestoreBuildChoice build;
        [ObservableProperty] private bool isSelected;
        [ObservableProperty] private string detail = string.Empty;
        [ObservableProperty] private string chip = string.Empty;
        [ObservableProperty] private bool chipWarn;
        [ObservableProperty] private bool hasChip;
        [ObservableProperty] private bool needsValues;

        public Asset Asset => Build.Asset;

        public RestoreTvRow(TvRestoreItem item, Func<GitHubRelease, Asset, Task<bool>>? collectSettings, Action changed)
        {
            _collectSettings = collectSettings;
            _changed = changed;
            App = item.App;
            Release = item.Release!;
            var tile = new InstalledApp(Release.Name, null, item.App.AppId, string.Empty, string.Empty, true);
            Initials = tile.Initials;
            Color = tile.FallbackColor;

            // A guessed build (or one the catalog dropped) can be any of the app's files with this id.
            var guess = item.App.Uncertain || item.VariantChanged;
            var files = guess
                ? Release.Assets.Where(a => string.Equals(a.AppId, item.Asset!.AppId, StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<Asset>();
            if (files.Count < 2)
                files = new List<Asset> { item.Asset! };
            Builds = files.Select(a => new RestoreBuildChoice(a, string.IsNullOrWhiteSpace(a.Variant) ? a.FileName : a.Variant.Trim())).ToList();
            build = Builds.FirstOrDefault(b => ReferenceEquals(b.Asset, item.Asset)) ?? Builds[0];
            _guess = guess && Builds.Count > 1;

            Refresh();
            isSelected = !NeedsValues;
        }

        private readonly bool _guess;

        partial void OnBuildChanged(RestoreBuildChoice value) => Refresh();
        partial void OnIsSelectedChanged(bool value) => _changed();

        /// <summary>The values this file asks for (#791) are there: saved on this computer, or in the profile.</summary>
        private bool ValuesComplete()
        {
            if (Asset.UserSettings is not { Fields.Count: > 0 } spec)
                return true;
            var saved = UserSettingsStore.Get(AppSettings.Default.AppUserSettingsJson, Asset.FileName)?.Values ?? App.Settings;
            return saved is not null && spec.Fields.All(f => UserSettingsStore.Validate(f, saved.TryGetValue(f.Key, out var v) ? v : null) is null);
        }

        private void Refresh()
        {
            var parts = new List<string>();
            var was = App.Version?.Trim();
            var now = Asset.PackageVersion?.Trim();
            if (!string.IsNullOrEmpty(was) && !string.IsNullOrEmpty(now) && !string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{was} → {now}");
            else if (!string.IsNullOrEmpty(now ?? was))
                parts.Add(now ?? was!);

            var asks = Asset.UserSettings is { Fields.Count: > 0 };
            NeedsValues = asks && !ValuesComplete();
            if (_guess)
            {
                parts.Add("lblRestoreBuildGuess".Localized());
                Chip = "lblRestoreChipCheckBuild".Localized();
                ChipWarn = true;
            }
            else if (!string.IsNullOrWhiteSpace(Asset.Variant))
            {
                parts.Add(string.Format("lblRestoreBuild".Localized(), Asset.Variant.Trim()));
            }

            if (NeedsValues)
            {
                parts.Add("lblRestoreValuesMissing".Localized());
                Chip = "lblRestoreChipAsks".Localized();
                ChipWarn = true;
            }
            else if (asks)
            {
                parts.Add("lblRestoreValuesSaved".Localized());
                if (!_guess)
                {
                    Chip = "lblRestoreChipSettings".Localized();
                    ChipWarn = false;
                }
            }
            HasChip = _guess || asks;
            Detail = string.Join(" · ", parts);
        }

        /// <summary>"Fill in…": the same form an install shows; with the values saved the row gets ticked.</summary>
        [RelayCommand]
        private async Task FillIn()
        {
            if (_collectSettings is null)
                return;
            // Values the profile carries are the starting point when this computer has none.
            if (App.Settings is { Count: > 0 } carried && Asset.UserSettings is { } spec &&
                UserSettingsStore.Get(AppSettings.Default.AppUserSettingsJson, Asset.FileName) is null)
            {
                AppSettings.Default.AppUserSettingsJson = UserSettingsStore.Set(AppSettings.Default.AppUserSettingsJson, Asset.FileName, spec, carried);
                AppSettings.Default.Save();
            }
            if (!await _collectSettings(Release, Asset))
                return;
            Refresh();
            if (!NeedsValues)
                IsSelected = true;
        }
    }

    /// <summary>An app of the list that cannot be installed again, and why.</summary>
    public sealed record RestoreBlockedRow(string Name, string Initials, string Reason);

    /// <summary>
    /// The restore sheet (#1069): a saved app list against today's catalog and the TV it goes to.
    /// Tick what comes back; what cannot come back says why instead of disappearing.
    /// </summary>
    public partial class RestoreTvViewModel : ObservableObject
    {
        public string Title { get; }
        public string Subtitle { get; }
        public ObservableCollection<RestoreTvRow> Ready { get; } = new();
        public ObservableCollection<RestoreBlockedRow> Blocked { get; } = new();
        public bool HasReady => Ready.Count > 0;
        public bool HasBlocked => Blocked.Count > 0;
        public string BlockedHeader { get; }
        public string SkippedText { get; }
        public bool HasSkipped => SkippedText.Length > 0;
        public string NothingText { get; }
        public bool HasNothing => !HasReady;

        [ObservableProperty] private string readyHeader = string.Empty;
        [ObservableProperty] private string restoreText = string.Empty;
        [ObservableProperty] private bool canRestore;

        public RestoreTvViewModel(TvProfile profile, IReadOnlyList<TvRestoreItem> plan, IReadOnlyList<GitHubRelease> catalog,
            string tvLabel, string tvIp, Func<GitHubRelease, Asset, Task<bool>>? collectSettings)
        {
            var from = string.IsNullOrWhiteSpace(profile.TvName) ? profile.TvIp ?? tvLabel : profile.TvName;
            Title = string.Format("lblRestoreTitle".Localized(), from);
            var date = profile.SavedAtUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentUICulture);
            var target = string.Equals(tvLabel, tvIp, StringComparison.OrdinalIgnoreCase) ? tvIp : $"{tvLabel} ({tvIp})";
            Subtitle = string.Format("lblRestoreSubtitle".Localized(), date, profile.Apps.Count, target);

            var sources = new HashSet<string>(catalog.Select(r => r.Source.Key), StringComparer.Ordinal);
            var skipped = new List<string>();
            foreach (var item in plan)
            {
                switch (item.State)
                {
                    case TvRestoreState.AlreadyOnTv:
                        skipped.Add(item.App.Name);
                        break;
                    case TvRestoreState.Ready when item.Release!.IsRoot:
                        Blocked.Add(Block(item.App, "lblRestoreBlockedRoot".Localized()));
                        break;
                    case TvRestoreState.Ready:
                        Ready.Add(new RestoreTvRow(item, collectSettings, Count));
                        break;
                    default:
                        var foreign = !string.IsNullOrWhiteSpace(item.App.Source) && item.App.Source != "official" && !sources.Contains(item.App.Source);
                        Blocked.Add(Block(item.App, foreign
                            ? string.Format("lblRestoreBlockedSource".Localized(), item.App.Source)
                            : "lblRestoreBlockedGone".Localized()));
                        break;
                }
            }

            BlockedHeader = string.Format("lblRestoreBlockedHeader".Localized(), Blocked.Count);
            SkippedText = skipped.Count switch
            {
                0 => string.Empty,
                1 => string.Format("lblRestoreSkippedOne".Localized(), skipped[0]),
                _ => string.Format("lblRestoreSkippedMany".Localized(), string.Join(", ", skipped)),
            };
            NothingText = skipped.Count > 0 && Blocked.Count == 0 ? "lblRestoreNothingAllThere".Localized() : "lblRestoreNothing".Localized();
            Count();
        }

        private static RestoreBlockedRow Block(TvProfileApp app, string reason) =>
            new(app.Name, new InstalledApp(app.Name, null, app.AppId, string.Empty, string.Empty, true).Initials, reason);

        private void Count()
        {
            var picked = Ready.Count(r => r.IsSelected);
            ReadyHeader = string.Format("lblRestoreReadyHeader".Localized(), picked, Ready.Count);
            RestoreText = picked == 1 ? "lblRestoreGoOne".Localized() : string.Format("lblRestoreGoMany".Localized(), picked);
            CanRestore = picked > 0;
        }

        /// <summary>What the user ticked, in list order, with the build picked for each.</summary>
        public IReadOnlyList<RestoreTvRow> Picked() => Ready.Where(r => r.IsSelected).ToList();
    }
}
