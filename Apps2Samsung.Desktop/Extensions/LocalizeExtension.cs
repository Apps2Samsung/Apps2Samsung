using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Apps2Samsung.Extensions
{
    /// <summary>
    /// Reactive source for <c>{l:Localize key}</c> XAML bindings. <see cref="Tick"/> changes on every
    /// language switch; a binding to it (via <see cref="LocalizeConverter"/>) re-resolves the key, so
    /// every localized element in open windows updates live. (Avalonia does not re-evaluate indexer
    /// bindings on notification, hence the tick + converter rather than an indexer.)
    /// </summary>
    public sealed class LocalizationProxy : INotifyPropertyChanged
    {
        public static LocalizationProxy Instance { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        private int _tick;

        /// <summary>Bumped on each language change to invalidate every <c>{l:Localize}</c> binding.</summary>
        public int Tick => _tick;

        public void Refresh()
        {
            _tick++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tick)));
        }
    }

    /// <summary>Resolves the localization key passed as the converter parameter to its current string.</summary>
    public sealed class LocalizeConverter : IValueConverter
    {
        public static readonly LocalizeConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => (parameter as string ?? string.Empty).Localized();

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// XAML markup extension: <c>Text="{l:Localize lblShowInstalledApps}"</c> resolves a localized
    /// string from <c>en.json</c> (and its translations) and updates live on a language change.
    /// Prefer this over hard-coded literals so every user-facing string is available to translators.
    /// </summary>
    public sealed class LocalizeExtension
    {
        public LocalizeExtension() { }

        public LocalizeExtension(string key) => Key = key;

        public string Key { get; set; } = string.Empty;

        public object ProvideValue(IServiceProvider serviceProvider)
            => new Binding(nameof(LocalizationProxy.Tick))
            {
                Source = LocalizationProxy.Instance,
                Mode = BindingMode.OneWay,
                Converter = LocalizeConverter.Instance,
                ConverterParameter = Key
            };
    }
}
