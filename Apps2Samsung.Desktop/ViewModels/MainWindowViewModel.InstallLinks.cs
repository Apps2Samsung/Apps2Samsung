using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers;
using Apps2Samsung.Models;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Install links (#1066): <c>apps2samsung://install?app=…</c> opens that app's sheet, with the file
    /// the link names selected and a line saying a link opened it; <c>apps2samsung://source?url=…</c>
    /// opens Add a source filled in. Nothing installs or is added without the user's click.
    /// </summary>
    public partial class MainWindowViewModel
    {
        // One link at a time: a second one while a sheet is open waits for it.
        private readonly System.Threading.SemaphoreSlim _linkGate = new(1, 1);

        private async Task OpenInstallLinkAsync(string text)
        {
            if (!InstallLink.TryParse(text, out var link) || link is null)
            {
                Trace.WriteLine("[links] not an install link this version reads");
                await _dialogService.ShowLinkProblemAsync(L("lblLinkBrokenTitle"), L("lblLinkBrokenBody"), L("lblOk"), L("lblLinkClose"));
                return;
            }
            await _linkGate.WaitAsync();
            try
            {
                BringToFront();
                if (link.Kind == InstallLinkKind.Source)
                {
                    SelectedSection = Section.Settings;
                    _settingsViewModel.SelectedPage = SettingsViewModel.Page.Installing;
                    await _settingsViewModel.Application.AddUserSourceFromLinkAsync(link.SourceUrl!);
                    return;
                }
                await OpenLinkedAppAsync(link);
            }
            finally
            {
                _linkGate.Release();
            }
        }

        private async Task OpenLinkedAppAsync(InstallLink link)
        {
            // A cold start: the catalog is still coming in.
            var waited = Stopwatch.StartNew();
            while ((IsLoading || _allReleases.Count == 0) && waited.Elapsed < TimeSpan.FromSeconds(90))
                await Task.Delay(250);
            if (_allReleases.Count == 0)
            {
                await _dialogService.ShowLinkProblemAsync(L("lblLinkNoCatalogTitle"), L("lblLinkNoCatalogBody"), L("lblOk"), L("lblLinkClose"));
                return;
            }

            SelectedSection = Section.Install;
            var match = InstallLink.Resolve(link, VisibleReleases);
            if (match is null)
            {
                // Listed, but hidden by a setting: say which, and offer the page that has it.
                if (InstallLink.Resolve(link, _allReleases) is { } hidden)
                {
                    // 18+ (#850) is a Settings > Installing choice: the button goes there, the user decides.
                    if (hidden.Entries.All(e => e.IsAdult))
                    {
                        if (await _dialogService.ShowLinkProblemAsync(L("lblLinkAdultTitle"),
                                string.Format(L("lblLinkAdultBody"), hidden.Name), L("lblOpenSettings"), L("lblLinkClose")))
                        {
                            SelectedSection = Section.Settings;
                            _settingsViewModel.SelectedPage = SettingsViewModel.Page.Installing;
                        }
                        return;
                    }
                    // A root app, hidden while the catalog's ⋯ > Show root apps is off: the button turns it on and opens it.
                    if (!AppSettings.Default.ShowRootApps && await _dialogService.ShowLinkProblemAsync(L("lblLinkRootTitle"),
                            string.Format(L("lblLinkRootBody"), hidden.Name), L("lblShowRootApps"), L("lblLinkClose")))
                    {
                        _settingsViewModel.Application.ShowRootApps = true;
                        await OpenLinkedAppAsync(link);
                    }
                    return;
                }
                if (await _dialogService.ShowLinkProblemAsync(L("lblLinkNotFoundTitle"),
                        string.Format(L("lblLinkNotFoundBody"), link.App), L("lblSearchCatalog"), L("lblLinkClose")))
                    SearchText = link.App!;
                return;
            }

            // Several sources offer it (#1018): never merged, the user picks one.
            var bySource = match.Entries.GroupBy(e => e.Source.Key).Select(g => g.First()).ToList();
            var release = bySource[0];
            if (bySource.Count > 1)
            {
                var choices = bySource.Select(r => (r,
                        $"{r.Name} · {CatalogSourceLabels.Name(r.Source, L)}",
                        (r.Assets.FirstOrDefault(a => a.IsDefault) ?? r.Assets.FirstOrDefault())?.PackageVersion is { Length: > 0 } v ? $"v{v}" : r.TagName))
                    .ToList();
                var picked = await _dialogService.ShowLinkSourcePickerAsync(
                    string.Format(L("lblLinkSourcesTitle"), match.Name), L("lblLinkSourcesBody"), choices, L("lblCancel"));
                if (picked is null)
                    return;
                release = picked;
            }
            if (match.FileMissing)
                Trace.WriteLine($"[links] {match.Name} has no file '{link.File}'; its default is selected");
            await OpenAppCoreAsync(RowFor(release), match.File, fromLink: true);
        }

        private static void BringToFront()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
                return;
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            // Windows does not let a background process take the focus: a moment on top asks for attention instead.
            window.Topmost = true;
            window.Topmost = false;
        }
    }
}
