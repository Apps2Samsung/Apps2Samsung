using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Extensions
{
    public static class LocalizationExtensions
    {
        private static ILocalizationService? _localizationService;

        public static void SetLocalizationService(ILocalizationService service)
        {
            _localizationService = service;
        }

        public static string Localized(this string key)
        {
            return _localizationService?.GetString(key) ?? key;
        }
    }
}
