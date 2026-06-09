using Apps2Samsung.Interfaces;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Registers TVApp's settings (channel list) as a section in the Settings window.
    /// </summary>
    public sealed class TvAppSettingsProvider : IAppSettingsProvider
    {
        public string ProviderId => "tvapp";
        public string DisplayName => "TVApp";
        public int SortOrder => 1;

        public ViewModelBase CreateSettingsViewModel()
            => App.Services.GetRequiredService<TvAppSettingsViewModel>();
    }
}
