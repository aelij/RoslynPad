using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace RoslynPad.Converters
{
    public class KeyBindToDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter != null && parameter is UI.KEY_BIND keybind)
                return UI.KeybindHelper.GetDescription(keybind, true);
            return DependencyProperty.UnsetValue;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
    public class KeySequenceToGestureConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            
            if (value == null || value is not string s || string.IsNullOrWhiteSpace(s))
                return DependencyProperty.UnsetValue;
            try
            {
                var res = (KeyGesture?)converter.ConvertFromString(s);
                if (res == null)
                    return DependencyProperty.UnsetValue;
                if (targetType == typeof(KeyGesture))
                    return res;
                if (targetType == typeof(ModifierKeys))
                    return res.Modifiers;
                if (targetType == typeof(Key))
                    return res.Key;
            }
            catch { }
            return DependencyProperty.UnsetValue;
        }
        private static KeyGestureConverter converter = new KeyGestureConverter();
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
