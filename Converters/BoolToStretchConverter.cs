using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ErrorChecker.Converters
{
    public class BoolToStretchConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isRealSize)
            {
                return isRealSize ? Stretch.None : Stretch.Uniform;
            }
            return Stretch.Uniform;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
