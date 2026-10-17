using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Settings for Camera PiP (dzikus/tizen-camera-pip): one to four cameras, the mosaic style and
    /// sound. Persisted as JSON in <see cref="AppSettings.CameraPipConfigJson"/> and written into the
    /// widget's <c>config.js</c> (<c>window.PIP_CONFIG</c>) at install time by the shared Camera PiP
    /// package patcher — the upstream package ships with no cameras and shows a dark screen without this.
    /// </summary>
    public partial class CameraPipSettingsViewModel : ViewModelBase, IDisposable
    {
        private readonly ILocalizationService _localizationService;
        private bool _loading;

        public ObservableCollection<CameraPipCameraEntry> Cameras { get; } = new();

        public bool HasCameras => Cameras.Count > 0;

        /// <summary>The app composes at most four cameras; the Add button goes away at the limit.</summary>
        public bool CanAddCamera => Cameras.Count < CameraPipSettings.MaxCameras;

        /// <summary>Mosaic styles by their config.js value; the combo shows them by localized name.</summary>
        public ObservableCollection<MosaicStyleOption> MosaicStyles { get; } = new();

        [ObservableProperty] private MosaicStyleOption? selectedMosaicStyle;
        [ObservableProperty] private bool sound = true;

        public string LblCameraPipSettings => _localizationService.GetString("lblCameraPipSettings");
        public string LblCameraPipHint => _localizationService.GetString("lblCameraPipHint");
        public string LblCameraPipOnlyNotice => _localizationService.GetString("lblCameraPipOnlyNotice");
        public string LblCameraName => _localizationService.GetString("lblCameraName");
        public string LblCameraVideoUrl => _localizationService.GetString("lblCameraVideoUrl");
        public string LblCameraMjpegUrl => _localizationService.GetString("lblCameraMjpegUrl");
        public string LblCameraPipUrlsHint => _localizationService.GetString("lblCameraPipUrlsHint");
        public string LblAddCamera => _localizationService.GetString("lblAddCamera");
        public string LblNoCameras => _localizationService.GetString("lblNoCameras");
        public string LblCameraPipMaxCameras => string.Format(_localizationService.GetString("lblCameraPipMaxCameras"), CameraPipSettings.MaxCameras);
        public string LblCameraPipMosaicStyle => _localizationService.GetString("lblCameraPipMosaicStyle");
        public string LblCameraPipMosaicStyleHint => _localizationService.GetString("lblCameraPipMosaicStyleHint");
        public string LblCameraPipSound => _localizationService.GetString("lblCameraPipSound");

        public CameraPipSettingsViewModel(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
            _localizationService.LanguageChanged += OnLanguageChanged;

            RebuildMosaicStyles();
            Load();
            Cameras.CollectionChanged += OnCamerasChanged;
        }

        private void Load()
        {
            _loading = true;
            try
            {
                var settings = CameraPipConfigInjector.ParseSettingsJson(AppSettings.Default.CameraPipConfigJson);
                foreach (var camera in settings.Cameras.Take(CameraPipSettings.MaxCameras))
                {
                    var entry = new CameraPipCameraEntry { Name = camera.Name, VideoUrl = camera.VideoUrl, MjpegUrl = camera.MjpegUrl };
                    entry.PropertyChanged += OnCameraPropertyChanged;
                    Cameras.Add(entry);
                }

                SelectedMosaicStyle = MosaicStyles.FirstOrDefault(m => m.Value == settings.MosaicStyle) ?? MosaicStyles[0];
                Sound = settings.Sound;
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnCamerasChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (CameraPipCameraEntry camera in e.NewItems)
                    camera.PropertyChanged += OnCameraPropertyChanged;

            if (e.OldItems != null)
                foreach (CameraPipCameraEntry camera in e.OldItems)
                    camera.PropertyChanged -= OnCameraPropertyChanged;

            OnPropertyChanged(nameof(HasCameras));
            OnPropertyChanged(nameof(CanAddCamera));
            Save();
        }

        private void OnCameraPropertyChanged(object? sender, PropertyChangedEventArgs e) => Save();

        partial void OnSelectedMosaicStyleChanged(MosaicStyleOption? value) => Save();
        partial void OnSoundChanged(bool value) => Save();

        private void Save()
        {
            if (_loading)
                return;

            var settings = new CameraPipSettings(
                Cameras.Select(c => new CameraPipCamera(c.Name, c.VideoUrl, c.MjpegUrl)).ToList(),
                SelectedMosaicStyle?.Value ?? CameraPipSettings.MosaicTiled,
                Sound);

            AppSettings.Default.CameraPipConfigJson = CameraPipConfigInjector.ToJson(settings);
            AppSettings.Default.Save();
        }

        [RelayCommand]
        private void AddCamera()
        {
            if (CanAddCamera)
                Cameras.Add(new CameraPipCameraEntry());
        }

        [RelayCommand]
        private void RemoveCamera(CameraPipCameraEntry? camera)
        {
            if (camera != null)
                Cameras.Remove(camera);
        }

        // The combo holds option objects so a language switch can rename them without losing the
        // selection: the value is what is persisted, the name is only what is shown.
        private void RebuildMosaicStyles()
        {
            var current = SelectedMosaicStyle?.Value;
            _loading = true;
            try
            {
                MosaicStyles.Clear();
                MosaicStyles.Add(new MosaicStyleOption(CameraPipSettings.MosaicTiled, _localizationService.GetString("lblCameraPipMosaicTiled")));
                MosaicStyles.Add(new MosaicStyleOption(CameraPipSettings.MosaicOverlay, _localizationService.GetString("lblCameraPipMosaicOverlay")));
                if (current != null)
                    SelectedMosaicStyle = MosaicStyles.FirstOrDefault(m => m.Value == current) ?? MosaicStyles[0];
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RebuildMosaicStyles();
            OnPropertyChanged(nameof(LblCameraPipSettings));
            OnPropertyChanged(nameof(LblCameraPipHint));
            OnPropertyChanged(nameof(LblCameraPipOnlyNotice));
            OnPropertyChanged(nameof(LblCameraName));
            OnPropertyChanged(nameof(LblCameraVideoUrl));
            OnPropertyChanged(nameof(LblCameraMjpegUrl));
            OnPropertyChanged(nameof(LblCameraPipUrlsHint));
            OnPropertyChanged(nameof(LblAddCamera));
            OnPropertyChanged(nameof(LblNoCameras));
            OnPropertyChanged(nameof(LblCameraPipMaxCameras));
            OnPropertyChanged(nameof(LblCameraPipMosaicStyle));
            OnPropertyChanged(nameof(LblCameraPipMosaicStyleHint));
            OnPropertyChanged(nameof(LblCameraPipSound));
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }

    /// <summary>A mosaic style as the combo shows it: the config.js value plus its localized name.</summary>
    public sealed record MosaicStyleOption(string Value, string Name);
}
