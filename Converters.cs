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
}