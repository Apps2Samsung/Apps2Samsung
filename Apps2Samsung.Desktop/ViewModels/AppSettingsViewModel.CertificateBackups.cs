using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Certificate;
using Apps2Samsung.Extensions;

namespace Apps2Samsung.ViewModels
{
    /// <summary>One signing profile on Settings › Backup, with whether it is in a backup (#1068).</summary>
    public sealed record CertificateBackupRow(string Name, string Detail, string Chip, bool IsBackedUp);

    // Settings › Backup: the automatic certificate backups (#1068). Core writes them by itself whenever
    // a certificate is made, renewed or imported; this shows whether every profile is in one, where they
    // go, and offers Back up now for when the automatic one failed.
    public partial class AppSettingsViewModel
    {
        public ObservableCollection<CertificateBackupRow> CertificateBackupRows { get; } = new();

        [ObservableProperty] private bool hasCertificates;
        [ObservableProperty] private bool certificatesSafe;
        [ObservableProperty] private bool certificatesNeedBackup;
        [ObservableProperty] private string certificateBackupTitle = string.Empty;
        [ObservableProperty] private string certificateBackupText = string.Empty;
        [ObservableProperty] private string certificateBackupLocation = string.Empty;
        [ObservableProperty] private string certificateBackupNote = string.Empty;

        private string? _certificateBackupFolder;

        private void InitCertificateBackups()
        {
            CertificateBackups.StatusChanged += () => Dispatcher.UIThread.Post(RefreshCertificateBackups);
            RefreshCertificateBackups();
        }

        /// <summary>Re-reads the store and the index. Also called when the Backup page is opened, so a backup deleted by hand shows.</summary>
        public void RefreshCertificateBackups()
        {
            CertificateBackupStatus? status;
            try
            {
                status = CertificateBackups.Status();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CertBackup] Status failed: {ex.Message}");
                status = null;
            }

            CertificateBackupRows.Clear();
            if (status is null)
            {
                HasCertificates = CertificatesSafe = CertificatesNeedBackup = false;
                return;
            }

            foreach (var row in status.Profiles)
            {
                var p = row.Profile;
                var detail = p.NotAfterUtc is { } until
                    ? string.Format("lblCertBackupValidUntil".Localized(), until.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentUICulture))
                    : string.Empty;
                var chip = row.BackedUpUtc is { } at
                    ? string.Format("lblCertBackupBackedUp".Localized(), at.ToLocalTime().ToString("d MMM", CultureInfo.CurrentUICulture))
                    : "lblCertBackupNotBackedUp".Localized();
                CertificateBackupRows.Add(new CertificateBackupRow(p.ProfileName, detail, chip, row.IsBackedUp));
            }

            HasCertificates = status.Profiles.Count > 0;
            var missing = status.NotBackedUp.Count();
            CertificatesNeedBackup = HasCertificates && missing > 0;
            CertificatesSafe = HasCertificates && missing == 0;

            CertificateBackupTitle = CertificatesSafe
                ? "lblCertBackupSafe".Localized()
                : missing == 1 ? "lblCertBackupMissingOne".Localized() : string.Format("lblCertBackupMissingMany".Localized(), missing);
            CertificateBackupText = CertificatesSafe && status.Latest is { } latest
                ? string.Format("lblCertBackupSafeHint".Localized(), latest.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentUICulture))
                : "lblCertBackupMissingHint".Localized();
            if (status.LastError is { } error)
                CertificateBackupText += "\n" + string.Format("lblCertBackupFailed".Localized(), error);

            CertificateBackupLocation = string.Format("hintCertBackupGoesTo".Localized(), status.DisplayLocation, CertificateBackups.Keep);
            _certificateBackupFolder = status.DisplayLocation;
        }

        [RelayCommand]
        private void BackUpCertificatesNow()
        {
            try
            {
                var entry = CertificateBackups.BackUpNow();
                CertificateBackupNote = entry is null ? string.Empty : string.Format("statusCertBackupSaved".Localized(), entry.FileName);
            }
            catch (Exception ex)
            {
                CertificateBackupNote = string.Format("lblCertBackupFailed".Localized(), ex.Message);
                Trace.WriteLine($"[CertBackup] Back up now failed: {ex}");
            }
            RefreshCertificateBackups();
        }

        [RelayCommand]
        private void OpenCertificateBackupFolder()
        {
            if (string.IsNullOrEmpty(_certificateBackupFolder))
                return;
            try
            {
                Directory.CreateDirectory(_certificateBackupFolder);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CertBackup] Cannot create {_certificateBackupFolder}: {ex.Message}");
                return;
            }
            OpenWithShell(_certificateBackupFolder);
        }
    }
}
