using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Apps2Samsung.Certificate;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Samsung;
using Apps2Samsung.Views.Controls;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// The certificate sheet. Opened (not awaited) by <see cref="Services.DialogService"/> once
    /// provisioning knows a Samsung sign-in is needed; <see cref="SignInChosen"/> says whether to go
    /// on, <see cref="CancelRequested"/> stops a sign-in already running in the browser. UI thread only.
    /// </summary>
    public partial class CertificateWindow : Window
    {
        private readonly TaskCompletionSource<bool> _signIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<CertificateStage, int> _index = new();
        private readonly Func<string, string> _localize = key => key;
        private readonly IDialogService? _dialogs;
        private readonly string? _tvDuid;
        private bool _signingIn;
        private bool _finished;

        public ObservableCollection<StepItem> Steps { get; } = new();
        public string TitleText { get; } = string.Empty;
        public string SubText { get; } = string.Empty;
        public string LevelText { get; } = string.Empty;
        public string IntroText { get; } = string.Empty;
        public string SignInText { get; } = string.Empty;
        public string CancelText { get; } = string.Empty;
        public string CloseText { get; } = string.Empty;
        /// <summary>The sign-in link for another browser, when the default one is refused by Samsung's page.</summary>
        public string CopyLinkText { get; } = string.Empty;
        public string SignInHelpText { get; } = string.Empty;
        public string ImportText { get; } = string.Empty;
        public string ImportHintText { get; } = string.Empty;

        /// <summary>True when the sign-in was replaced by a certificate imported on this sheet (#921).</summary>
        public bool Imported { get; private set; }

        /// <summary>Cancel while the browser sign-in runs.</summary>
        public event EventHandler? CancelRequested;

        public CertificateWindow()
        {
            InitializeComponent();
        }

        public CertificateWindow(string tvName, CertificatePrivilegeLevel level, Func<string, string> localize,
            IDialogService? dialogs = null, string? tvDuid = null) : this()
        {
            _localize = localize;
            _dialogs = dialogs;
            _tvDuid = tvDuid;
            ImportText = localize("lblImportCertificate");
            ImportHintText = localize("certSheetImportHint");
            TitleText = localize("lblStepCertificate");
            SubText = string.Format(localize("certSheetFor"), tvName);
            LevelText = localize(CertificateStages.LevelKey(level));
            IntroText = localize("certSheetIntro");
            SignInText = localize("lblSignInWithSamsung");
            CancelText = localize("lblCancel");
            CloseText = localize("btn_Close");
            CopyLinkText = localize("lblCopySignInLink");
            SignInHelpText = localize("certSignInBrowserHelp");

            foreach (var item in StepList.Build(CertificateStages.Steps.Select(s => localize(s.TitleKey)), keepDoneDetails: true))
                Steps.Add(item);
            for (var i = 0; i < CertificateStages.Steps.Length; i++)
                _index[CertificateStages.Steps[i].Stage] = i;

            // The device ID is read before anyone knows a sign-in is needed, so it is done by now.
            Steps[_index[CertificateStage.SignIn]].Detail = localize("certStageSignInHintBrowser");
            StepList.SetCurrent(Steps, _index[CertificateStage.SignIn]);
            DataContext = this;
            Closed += OnClosed;
        }

        /// <summary>True once the user chose Sign in with Samsung; false on Cancel or close.</summary>
        public Task<bool> SignInChosen => _signIn.Task;

        public void SetStage(CertificateStage stage)
        {
            var index = stage == CertificateStage.Done ? Steps.Count : _index[stage];
            if (index > _index[CertificateStage.SignIn])
            {
                // Signed in: from here Samsung is making the certificate and there is nothing to stop.
                _signingIn = false;
                WaitPanel.IsVisible = false;
                Steps[_index[CertificateStage.SignIn]].Detail = null;
            }
            StepList.SetCurrent(Steps, index);
        }

        public void Report(string message)
        {
            if (Steps.FirstOrDefault(s => s.IsActive) is { } current)
                current.Detail = message;
        }

        public void Fail(string message)
        {
            _signingIn = false;
            StepList.Fail(Steps, message);
            SignInButtons.IsVisible = false;
            WaitPanel.IsVisible = false;
            CloseButton.IsVisible = true;
        }

        /// <summary>All steps ticked, a moment to see them, then the window goes.</summary>
        public async Task FinishAsync()
        {
            _finished = true;
            SetStage(CertificateStage.Done);
            await Task.Delay(700);
            Close();
        }

        private void OnSignInClick(object? sender, RoutedEventArgs e)
        {
            _signingIn = true;
            SignInButtons.IsVisible = false;
            WaitPanel.IsVisible = true;
            Steps[_index[CertificateStage.SignIn]].Detail = _localize("certStageSignInWaiting");
            _signIn.TrySetResult(true);
        }

        // The same URL the browser was sent to (fixed state, loopback callback), so pasting it into
        // another browser completes the sign-in on the listener that is already waiting. Firefox
        // with strict tracking protection is refused by Samsung's page ("Access Denied"); Safari,
        // Chrome and Edge pass.
        private async void OnCopyLinkClick(object? sender, RoutedEventArgs e)
        {
            if (Clipboard is null)
                return;
            await Clipboard.SetTextAsync(SamsungOAuth.BuildAuthorizeUrl(SamsungOAuth.LoopbackRedirectUri));
            CopyLinkButton.Content = _localize("lblCopied");
            await Task.Delay(1500);
            CopyLinkButton.Content = CopyLinkText;
        }

        // Import a certificate exported on another device instead of signing in (#921): pick the
        // file, show what it holds and what it replaces, write it. When it covers this TV the
        // provisioning run resumes as if Sign in had been chosen and finds the profile on disk; when
        // it does not, the author certificate is now here and Sign in only extends it to this TV.
        private async void OnImportClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = _localize("certArchiveImportTitle"),
                    AllowMultiple = false,
                    FileTypeFilter = CertificateArchiveFiles.PickerTypes(_localize),
                });
                var file = files.FirstOrDefault();
                if (file is null)
                    return;

                var outcome = await CertificateArchiveFiles.ImportWithPreviewAsync(file, _tvDuid, _localize,
                    (title, text) => _dialogs is null
                        ? Task.FromResult(true)
                        : _dialogs.ShowConfirmationAsync(title, text, _localize("lblImport"), _localize("lblCancel"), this));
                if (outcome is null)
                    return;

                Imported = true;
                if (outcome.CoversTv)
                {
                    SignInButtons.IsVisible = false;
                    Steps[_index[CertificateStage.SignIn]].Detail = string.Format(_localize("statusCertificateImported"), outcome.FileCount);
                    _signIn.TrySetResult(true);
                }
                else
                {
                    Steps[_index[CertificateStage.SignIn]].Detail = _localize("certArchiveImportedNotCovering");
                    ImportButton.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                if (_dialogs is not null)
                    await _dialogs.ShowErrorAsync(string.Format(_localize("statusCertificateImportFailed"), ex.Message));
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

        private void OnClosed(object? sender, EventArgs e)
        {
            _signIn.TrySetResult(false);
            if (_signingIn && !_finished)
                CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
