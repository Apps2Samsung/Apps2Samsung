using Apps2Samsung.Interfaces;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Registers TizenTube's settings (the Cobalt proxy address) as a section in the Settings window.
    /// </summary>
    public sealed class TizenTubeSettingsProvider : IAppSettingsProvider
    {
        public string ProviderId => "tizentube";
        public string DisplayName => "TizenTube";
        public int SortOrder => 2;

        public ViewModelBase CreateSettingsViewModel()
            => App.Services.GetRequiredService<TizenTubeSettingsViewModel>();
    }
}
