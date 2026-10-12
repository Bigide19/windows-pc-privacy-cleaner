using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PcPrivacyCleaner.Infrastructure
{
    /// <summary>true -> Collapsed, false -> Visible (the built-in converter only goes the other way).</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool && (bool)value) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
