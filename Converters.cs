using System;
using System.Globalization;
using System.Windows.Data;

namespace BattleTrackerOverlay
{
    public class StringFormatConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
            {
                // CHANGED: This is the WPF-standard way to handle a failed conversion.
                return Binding.DoNothing;
            }
            return string.Format((string)parameter, value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IntToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue && parameter is string strParam && int.TryParse(strParam, out var compareValue))
            {
                return intValue == compareValue;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue && parameter is string strParam && int.TryParse(strParam, out var intValue))
            {
                return intValue;
            }
            return Binding.DoNothing;
        }
    }
}