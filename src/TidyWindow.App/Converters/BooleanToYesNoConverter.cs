using System;
using System.Globalization;
using System.Windows.Data;

namespace TidyWindow.App.Converters;

public sealed class BooleanToYesNoConverter : IValueConverter
{
    public string TrueText { get; set; } = "Installed";

    public string FalseText { get; set; } = "Missing";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolean)
        {
            return boolean ? TrueText : FalseText;
        }

        return FalseText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
