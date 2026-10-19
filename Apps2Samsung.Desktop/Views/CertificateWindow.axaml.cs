using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
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

        /// <summary>Cancel while the browser sign-in runs.</summary>
        public event EventHandler? CancelRequested;

        public CertificateWindow()
        {
            InitializeComponent();
        }

        public CertificateWindow(string tvName, CertificatePrivilegeLevel level, Func<string, string> localize) : this()
        {
            _localize = localize;
            TitleText = localize("lblStepCertificate");
            SubText = string.Format(localize("certSheetFor"), tvName);
            LevelText = localize(CertificateStages.LevelKey(level));
            IntroText = localize("certSheetIntro");
            SignInText = localize("lblSignInWithSamsung");
            CancelText = localize("lblCancel");
            CloseText = localize("btn_Close");

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
                CancelWaitButton.IsVisible = false;
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
            CancelWaitButton.IsVisible = false;
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
            CancelWaitButton.IsVisible = true;
            Steps[_index[CertificateStage.SignIn]].Detail = _localize("certStageSignInWaiting");
            _signIn.TrySetResult(true);
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
