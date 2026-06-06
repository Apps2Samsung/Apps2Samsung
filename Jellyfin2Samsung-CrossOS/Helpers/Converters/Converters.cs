using Avalonia.Data.Converters;
using Avalonia.Media;
using Apps2Samsung.Models;
using System;
using System.Globalization;

namespace Apps2Samsung.Helpers.Converters
{
    public class TvLogStatusToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value switch
            {
                TvLogConnectionStatus.Connected => new SolidColorBrush(Color.Parse("#27AE60")),
                TvLogConnectionStatus.Listening => new SolidColorBrush(Color.Parse("#2980B9")),
                TvLogConnectionStatus.NoConnections => new SolidColorBrush(Color.Parse("#E67E22")),
                TvLogConnectionStatus.Stopped => new SolidColorBrush(Color.Parse("#7F8C8D")),
                _ => Brushes.Gray
            };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class StringEqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not GitHubRelease release)
                return false;

            return release.Name == parameter?.ToString();
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class StringNotEqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not GitHubRelease release)
                return true;

            return release.Name != parameter?.ToString();
        }



        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
