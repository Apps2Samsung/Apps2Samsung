using Apps2Samsung.Interfaces;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Registers ImmiTV's settings (Immich server + login) as a section in the Settings window.
    /// </summary>
    public sealed class ImmiTvSettingsProvider : IAppSettingsProvider
    {
        public string ProviderId => "immitv";
        public string DisplayName => "ImmiTV";
        public int SortOrder => 2;

        public ViewModelBase CreateSettingsViewModel()
            => App.Services.GetRequiredService<ImmiTvSettingsViewModel>();
    }
}
