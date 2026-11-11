using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Catalog;
using Apps2Samsung.Extensions;
using Apps2Samsung.Models;

namespace Apps2Samsung.ViewModels
{
    public enum UpdateAllState { Updated, Failed, Skipped, NotStarted }

    /// <summary>What Update all did with one app (#1067). <paramref name="Detail"/>: the new version, or why not.</summary>
    public sealed record UpdateAllOutcome(string Name, UpdateAllState State, string? Detail);

    // Update all (#1067, mockup B): a green banner above the list while apps have a newer version in the
    // catalog, the run one app at a time with "Stop after this one", then a summary that names what failed.
    // Only apps whose catalog version is clearly higher are in it; an app whose versions differ but cannot
    // be ordered keeps its own Update.
    public partial class InstalledAppsViewModel
    {
        private Func<IReadOnlyList<(GitHubRelease Release, Asset Asset)>, Action<int, string>, Func<bool>, Task<IReadOnlyList<UpdateAllOutcome>>>? _updateAll;
        private bool _stopUpdateAll;

        /// <summary>Set by the main window, which runs the installs.</summary>
        public void UseUpdateAll(Func<IReadOnlyList<(GitHubRelease Release, Asset Asset)>, Action<int, string>, Func<bool>, Task<IReadOnlyList<UpdateAllOutcome>>> updateAll) =>
            _updateAll = updateAll;

        [ObservableProperty] private int updatesCount;
        [ObservableProperty] private string updatesTitle = string.Empty;
        [ObservableProperty] private string updatesNames = string.Empty;
        [ObservableProperty] private bool showUpdatesBanner;

        [ObservableProperty] private bool isUpdatingAll;
        [ObservableProperty] private string updateAllProgressText = string.Empty;
        [ObservableProperty] private double updateAllProgress;
        [ObservableProperty] private bool stopAfterCurrent;

        [ObservableProperty] private bool hasUpdateAllSummary;
        [ObservableProperty] private bool updateAllSummaryWarn;
        [ObservableProperty] private string updateAllSummaryTitle = string.Empty;
        [ObservableProperty] private string updateAllSummaryText = string.Empty;

        // The apps Update all takes: a match whose catalog version is higher, in list order.
        private List<(GitHubRelease Release, Asset Asset, string Name)> UpdateTargets() =>
            Apps.Select(vm => vm.App)
                .Where(a => a.HasUpdate && _matches.TryGetValue(a.TizenId, out var m) && m.State == CatalogVersionState.UpdateAvailable)
                .Select(a => (_matches[a.TizenId].Release, _matches[a.TizenId].Asset, a.DisplayName))
                .ToList();

        /// <summary>Called after the list is read: the banner's count and names.</summary>
        private void RefreshUpdatesBanner()
        {
            var targets = UpdateTargets();
            UpdatesCount = targets.Count;
            UpdatesTitle = targets.Count == 1 ? "lblUpdateAllOne".Localized() : string.Format("lblUpdateAllMany".Localized(), targets.Count);
            UpdatesNames = string.Join(", ", targets.Select(t => t.Name));
            ShowUpdatesBanner = targets.Count > 0 && _updateAll is not null && !IsUpdatingAll && !HasUpdateAllSummary;
        }

        [RelayCommand]
        private async Task UpdateAll()
        {
            if (_updateAll is null || IsUpdatingAll || IsBusy)
                return;
            var targets = UpdateTargets();
            if (targets.Count == 0)
                return;

            _stopUpdateAll = false;
            StopAfterCurrent = false;
            IsUpdatingAll = true;
            ShowUpdatesBanner = false;
            HasUpdateAllSummary = false;
            IReadOnlyList<UpdateAllOutcome> outcomes;
            try
            {
                outcomes = await _updateAll(
                    targets.Select(t => (t.Release, t.Asset)).ToList(),
                    (index, name) => Dispatcher.UIThread.Post(() =>
                    {
                        UpdateAllProgressText = string.Format("lblUpdateAllProgress".Localized(), index + 1, targets.Count, name);
                        UpdateAllProgress = 100.0 * index / targets.Count;
                    }),
                    () => _stopUpdateAll);
            }
            catch (Exception ex)
            {
                outcomes = new[] { new UpdateAllOutcome(UpdateAllProgressText, UpdateAllState.Failed, ex.Message) };
            }
            finally
            {
                IsUpdatingAll = false;
            }

            ShowSummary(outcomes);
            await Load();
        }

        [RelayCommand]
        private void StopUpdateAll()
        {
            _stopUpdateAll = true;
            StopAfterCurrent = true;
        }

        [RelayCommand]
        private void DismissUpdateAllSummary()
        {
            HasUpdateAllSummary = false;
            RefreshUpdatesBanner();
        }

        private void ShowSummary(IReadOnlyList<UpdateAllOutcome> outcomes)
        {
            var updated = outcomes.Count(o => o.State == UpdateAllState.Updated);
            var failed = outcomes.Where(o => o.State == UpdateAllState.Failed).ToList();
            var skipped = outcomes.Where(o => o.State is UpdateAllState.Skipped or UpdateAllState.NotStarted).ToList();

            UpdateAllSummaryWarn = failed.Count > 0;
            UpdateAllSummaryTitle = failed.Count == 0
                ? string.Format("lblUpdateAllDone".Localized(), updated)
                : string.Format("lblUpdateAllDoneFailed".Localized(), updated, failed.Count);
            var lines = new List<string>();
            foreach (var f in failed)
                lines.Add($"{f.Name}: {f.Detail}");
            foreach (var s in skipped)
                lines.Add(s.State == UpdateAllState.NotStarted
                    ? string.Format("lblUpdateAllNotStarted".Localized(), s.Name)
                    : $"{s.Name}: {s.Detail}");
            UpdateAllSummaryText = string.Join("\n", lines);
            HasUpdateAllSummary = true;
        }
    }
}
