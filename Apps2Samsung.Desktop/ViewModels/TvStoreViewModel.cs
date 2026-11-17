using Apps2Samsung.Certificate;
using Apps2Samsung.Extensions;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.TvStore;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// The TV-Store section (#1041): the catalog on the TV itself, out of the toolbox and into the
    /// sidebar. A setup check read off the TV without changing anything there (TV-Store installed and
    /// its version, what Host PC IP means for it), then what this app does with TV-Store: hand it the
    /// certificate (Arm, #844), send it signed repositories (#1018) and send it a package (#979).
    /// One per selected TV, like the On the TV and Remote sections.
    /// </summary>
    public partial class TvStoreViewModel : ViewModelBase
    {
        private readonly string _tvIp;

        // The developer connection, where this head has one. Arm and Send sources need it; the setup
        // check and Send a package work without it.
        private readonly ISdbEngine? _sdb;

        // The head's certificate flow for this TV: the signing profile an install would use, made
        // (with the Samsung sign-in) when none covers the set yet; null when that was cancelled.
        private readonly Func<bool, Action<string>, Task<SigningProfile?>>? _signingProfile;

        // The package picker and the install's customization step, from the main window.
        private readonly TvToolboxPackageHooks? _packageHooks;

        // Opens TV-Store's sheet in the catalog (#888); null when the catalog does not list it.
        private readonly Action? _showTvStoreInCatalog;

        public TvStoreViewModel(
            string tvIp, string tvLabel, ISdbEngine? sdb,
            Func<bool, Action<string>, Task<SigningProfile?>>? signingProfile,
            TvToolboxPackageHooks? packageHooks, Action? showTvStoreInCatalog)
        {
            _tvIp = tvIp;
            TvLabel = tvLabel;
            _sdb = sdb;
            _signingProfile = signingProfile;
            _packageHooks = packageHooks;
            _showTvStoreInCatalog = showTvStoreInCatalog;
        }

        public string TvLabel { get; }

        public bool HasSdb => _sdb is not null;

        [ObservableProperty]
        private bool isBusy;

        [ObservableProperty]
        private bool isChecking;

        /// <summary>The card's headline: "TV-Store 0.3.14" once the TV said, else just the name.</summary>
        [ObservableProperty]
        private string headline = "TV-Store";

        /// <summary>The card's line under the headline: the first check, in words.</summary>
        [ObservableProperty]
        private string subline = string.Empty;

        /// <summary>The card's dot: a design token, Ok, Warn, Bad, Accent (checking) or Muted.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LedBrush))]
        private string ledToken = "Muted";

        public Avalonia.Media.IBrush LedBrush => Apps2Samsung.Styles.DesignTheme.Brush(LedToken);

        /// <summary>The setup check's lines, in words.</summary>
        public ObservableCollection<TvStoreCheckRow> Checks { get; } = new();

        /// <summary>TV-Store is missing or too old, and the catalog lists it: one button to its sheet.</summary>
        [ObservableProperty]
        private bool canShowTvStoreInCatalog;

        /// <summary>The Arm line: each step of arming, then the verdict (#844).</summary>
        [ObservableProperty]
        private string tvStoreStatus = string.Empty;

        [ObservableProperty]
        private string sourcesStatus = string.Empty;

        [ObservableProperty]
        private string packageStatus = string.Empty;

        /// <summary>Raised when the section hands over to the catalog's TV-Store sheet.</summary>
        public event Action? OnRequestClose;

        /// <summary>
        /// The setup check: reads the TV (app list, REST version, Host PC IP) and turns it into
        /// lines. Changes nothing on the TV. Runs when the section is first shown and on Check again.
        /// </summary>
        [RelayCommand]
        private async Task Load()
        {
            if (IsChecking)
                return;
            IsChecking = true;
            Subline = "lblTvStoreChecking".Localized();
            LedToken = "Accent";
            try
            {
                var renames = Apps2Samsung.Helpers.AppSettings.Default.PackageIdRenamesJson;
                var overview = await TvStoreSetupCheck.ReadAsync(_tvIp, _sdb, renames);
                var lines = TvStoreSetupCheck.Lines(overview, _tvIp, Apps2Samsung.Sdb.SdbEndpoint.LocalAddresses());

                Checks.Clear();
                foreach (var line in lines)
                    Checks.Add(new TvStoreCheckRow(line));

                Headline = overview.Version is null ? "TV-Store" : $"TV-Store {overview.Version}";
                Subline = Checks.FirstOrDefault()?.Text ?? string.Empty;
                // The dot is the worst line: blocking, then worth a look, then not known, else all fine.
                LedToken = lines.Any(l => l.Level == TvStoreCheckLevel.Bad) ? "Bad"
                    : lines.Any(l => l.Level == TvStoreCheckLevel.Warn) ? "Warn"
                    : lines.Any(l => l.Level == TvStoreCheckLevel.Unknown) ? "Muted"
                    : "Ok";
                CanShowTvStoreInCatalog = _showTvStoreInCatalog is not null &&
                    overview.State is TvStoreInstallState.NotInstalled or TvStoreInstallState.Outdated;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[tvstore] setup check failed: {ex}");
                Subline = string.Format("lblToolboxTvStoreFailed".Localized(), ex.Message);
                LedToken = "Muted";
            }
            finally
            {
                IsChecking = false;
            }
        }

        [RelayCommand]
        private void ShowTvStoreInCatalog()
        {
            if (_showTvStoreInCatalog is null)
                return;
            _showTvStoreInCatalog();
            OnRequestClose?.Invoke();
        }

        /// <summary>
        /// Hands the signing certificate to the TV-Store app on the TV (#844, phase 3): the profile
        /// the head installs with, as PEM, over TV-Store's page in debug mode. Once armed, the TV
        /// signs and installs catalog apps on its own. TV-Store has to be on the set already; it
        /// is installed from the catalog like any package.
        /// </summary>
        [RelayCommand]
        private async Task ArmTvStore()
        {
            if (_sdb is null)
            {
                TvStoreStatus = "lblToolboxAgentNeedsSdb".Localized();
                return;
            }

            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                // This device's package-id renames (#839): a TV-Store installed under a new id is TV-Store too.
                var renames = Apps2Samsung.Helpers.AppSettings.Default.PackageIdRenamesJson;
                var state = await TvStorePackage.GetInstallStateAsync(_sdb, _tvIp, renames);
                if (state != TvStoreInstallState.Installed)
                {
                    TvStoreStatus = (state == TvStoreInstallState.Outdated ? "lblToolboxTvStoreOutdated" : "lblToolboxTvStoreNotInstalled").Localized();
                    // The message stays; one more button takes the user to TV-Store's catalog sheet (#888).
                    CanShowTvStoreInCatalog = _showTvStoreInCatalog is not null;
                    return;
                }

                TvStoreStatus = "lblToolboxTvStoreCertificate".Localized();
                var profile = _signingProfile is null ? null : await _signingProfile(false, message => TvStoreStatus = message);
                if (profile is null)
                {
                    TvStoreStatus = "lblToolboxTvStoreNoCertificate".Localized();
                    return;
                }
                // Every profile on this PC goes along, the covering one first (#44): the level
                // profiles and imports under the certificate store, and the bundled one older sets
                // get, so TV-Store can update an app whichever of them installed it.
                var pems = SigningCertificatePem.OpenAll(profile,
                    SigningCertificatePem.ProfilesIn(Apps2Samsung.Helpers.AppSettings.CertificatePath, Apps2Samsung.Helpers.AppSettings.BundledCertificatePath));

                var progress = new Progress<string>(key => TvStoreStatus = key.Localized());
                await using var store = await TvStoreClient.AttachAsync(_sdb, _tvIp, progress, renames);
                TvStoreStatus = "lblToolboxTvStoreArming".Localized();
                await store.ArmAsync(pems);
                var status = await store.StatusAsync();
                // What is left to do depends on the Host PC IP the TV has right now (#960).
                var tv = await TizenDeveloperInfo.ReadAsync(new NetworkDevice { IpAddress = _tvIp });
                var next = TvStoreNextSteps.For(tv.DeveloperIP, status.OwnIp);
                TvStoreStatus = string.Format(TvStoreNextSteps.MessageKey(next).Localized(), store.Version, status.OwnIp ?? _tvIp);
            }
            catch (TvStoreUnreachableException ex)
            {
                // The listing failed, so TV-Store may well be there: point at Developer Mode, not the catalog (#889).
                System.Diagnostics.Trace.WriteLine($"[tvstore] arm: no app listing: {ex.Reason}");
                TvStoreStatus = string.Format("lblToolboxTvStoreUnreachable".Localized(), ex.Reason);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[tvstore] arm failed: {ex}");
                TvStoreStatus = string.Format("lblToolboxTvStoreFailed".Localized(), ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---- Send sources to TV (#1018, TV-Store#82) ----

        /// <summary>One signed repository in the list, ticked to go to the TV; a changed key can't be sent until reviewed.</summary>
        public sealed partial class TvSourceChoice : ObservableObject
        {
            public TvSourceChoice(Apps2Samsung.Catalog.UserSource source, bool canSend, string note)
            {
                Source = source;
                Name = Apps2Samsung.Catalog.UserSources.DisplayName(source);
                CanSend = canSend;
                isChecked = canSend;
                Note = note;
            }
            public Apps2Samsung.Catalog.UserSource Source { get; }
            public string Name { get; }
            public bool CanSend { get; }
            [ObservableProperty]
            private bool isChecked;
            [ObservableProperty]
            private string note;
        }

        public ObservableCollection<TvSourceChoice> TvSources { get; } = new(LoadTvSources());
        public bool HasTvSources => TvSources.Count > 0;
        /// <summary>The Send button is always shown; it works once there is a signed repository and a connection.</summary>
        public bool CanSendSources => HasTvSources && HasSdb;

        // The approved signed repositories; one whose key changed since is listed but can't be ticked.
        private static System.Collections.Generic.IEnumerable<TvSourceChoice> LoadTvSources() =>
            Apps2Samsung.Catalog.UserSources.Parse(Apps2Samsung.Helpers.AppSettings.Default.UserSourcesJson)
                .Where(Apps2Samsung.Catalog.UserSources.IsApprovedRepository)
                .Select(src =>
                {
                    var keyChanged = Apps2Samsung.Catalog.Repositories.SignedRepositories.LastLoad(src.Url)?.State
                        == Apps2Samsung.Catalog.Repositories.RepositoryState.KeyChanged;
                    return new TvSourceChoice(src, !keyChanged, keyChanged ? "lblToolboxSendSourcesReview".Localized() : string.Empty);
                });

        /// <summary>
        /// Hands the ticked signed repositories to TV-Store with the keys approved here; the TV asks
        /// the person at it for each, and this waits for the answers (up to five minutes).
        /// </summary>
        [RelayCommand]
        private async Task SendSourcesToTv()
        {
            if (_sdb is null)
            {
                SourcesStatus = "lblToolboxAgentNeedsSdb".Localized();
                return;
            }
            var chosen = TvSources.Where(c => c.CanSend && c.IsChecked).Select(c => c.Source).ToList();
            if (chosen.Count == 0)
            {
                SourcesStatus = "lblToolboxSendSourcesNone".Localized();
                return;
            }
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                var renames = Apps2Samsung.Helpers.AppSettings.Default.PackageIdRenamesJson;
                var progress = new Progress<string>(key => SourcesStatus = key.Localized());
                await using var store = await TvStoreClient.AttachAsync(_sdb, _tvIp, progress, renames);
                SourcesStatus = "lblToolboxSendSourcesWaiting".Localized();
                var results = await store.AddSourcesAsync(chosen);
                foreach (var r in results)
                {
                    var choice = TvSources.FirstOrDefault(c => string.Equals(c.Source.RepositoryId, r.RepositoryId, StringComparison.Ordinal));
                    if (choice is not null)
                        choice.Note = r.Added ? "lblSourceOnTv".Localized() : r.Error ?? string.Empty;
                }
                var lines = new System.Collections.Generic.List<string> { string.Format("lblToolboxSendSourcesDone".Localized(), results.Count(r => r.Added), results.Count) };
                lines.AddRange(results.Where(r => !r.Added).Select(r => string.Format("lblToolboxSendSourcesNotAdded".Localized(), r.Name ?? r.RepositoryId, r.Error)));
                SourcesStatus = string.Join(Environment.NewLine, lines);
            }
            catch (TvStoreNotInstalledException ex)
            {
                SourcesStatus = (ex.IsOutdated ? "lblToolboxTvStoreOutdated" : "lblToolboxTvStoreNotInstalled").Localized();
            }
            catch (TvStoreUnreachableException ex)
            {
                SourcesStatus = string.Format("lblToolboxTvStoreUnreachable".Localized(), ex.Reason);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[tvstore] send sources failed: {ex}");
                SourcesStatus = ex is NotSupportedException ? ex.Message : string.Format("lblToolboxTvStoreFailed".Localized(), ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Send a package (#979): a .wgt/.tpk from disk goes to TV-Store's upload page on the TV,
        /// customized as an install would (the values asked before install) but not signed; TV-Store
        /// is opened on the TV when it is not running, asks on the screen and signs it itself. No
        /// developer connection from this PC is involved, so it works while Host PC IP is 127.0.0.1.
        /// </summary>
        [RelayCommand]
        private async Task SendToTvStore()
        {
            if (_packageHooks is null || IsBusy)
                return;

            var input = await _packageHooks.PickPackage();
            if (string.IsNullOrWhiteSpace(input))
                return;
            if (!PackageSigner.IsPackage(input))
            {
                PackageStatus = "statusChooseWgt".Localized();
                return;
            }

            IsBusy = true;
            try
            {
                Action<string> report = line => PackageStatus = Apps2Samsung.Localization.ProgressText.Localize(line, k => k.Localized());
                if (!await TvStoreHandoff.EnsureRunningAsync(_tvIp, report))
                {
                    PackageStatus = "tvStoreNotRunning".Localized();
                    return;
                }

                // A copy: the customization edits the file in place, and the user's file stays as it was.
                using var staged = Apps2Samsung.Helpers.Core.StagedPackage.Create(input);
                await _packageHooks.Customize(_tvIp, staged.FilePath);
                var sent = await TvStoreHandoff.SendAsync(_tvIp, staged.FilePath, report);
                PackageStatus = string.Format((sent.TvStoreOpened ? "tvStoreSentBody" : "tvStoreSentBodyClosed").Localized(), sent.Upload.Name);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[tvstore] send package failed: {ex}");
                PackageStatus = string.Format("lblToolboxTvStoreFailed".Localized(), ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>One line of the setup check, in words, with its mark.</summary>
    public sealed class TvStoreCheckRow
    {
        public TvStoreCheckRow(TvStoreCheck check)
        {
            Level = check.Level;
            Text = check.Args.Length == 0 ? check.Key.Localized() : string.Format(check.Key.Localized(), check.Args);
        }

        public TvStoreCheckLevel Level { get; }
        public string Text { get; }
        public bool IsOk => Level == TvStoreCheckLevel.Ok;
        public bool IsWarn => Level == TvStoreCheckLevel.Warn;
        public bool IsBad => Level == TvStoreCheckLevel.Bad;
        public bool IsUnknown => Level == TvStoreCheckLevel.Unknown;
    }
}
