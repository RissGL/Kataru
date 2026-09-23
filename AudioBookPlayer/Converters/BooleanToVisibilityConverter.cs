using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AudioBookPlayer.Converters
{
    /// <summary>
    /// bool → Visibility。Inverse=True 时反过来（true 变折叠）。
    /// </summary>
    public sealed class BooleanToVisibilityConverter : IValueConverter
    {
        public bool Inverse { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            if (Inverse)
            {
                flag = !flag;
            }

            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var visible = value is Visibility visibility && visibility == Visibility.Visible;
            return Inverse ? !visible : visible;
        }
    }
}
