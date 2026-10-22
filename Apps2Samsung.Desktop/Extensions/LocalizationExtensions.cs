using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Extensions
{
    public static class LocalizationExtensions
    {
        private static ILocalizationService? _localizationService;

        public static void SetLocalizationService(ILocalizationService service)
        {
            _localizationService = service;
            // Keep {l:Localize} bindings live across language switches.
            service.LanguageChanged += (_, _) => LocalizationProxy.Instance.Refresh();
        }

        public static string Localized(this string key)
        {
            return _localizationService?.GetString(key) ?? key;
        }
    }
}
