using System;

namespace Apps2Samsung.Interfaces
{
    public interface IThemeService
    {
        bool IsDarkMode { get; }
        event EventHandler<bool>? ThemeChanged;
        void SetTheme(bool isDarkMode);
        void ApplyTheme();
    }
}
