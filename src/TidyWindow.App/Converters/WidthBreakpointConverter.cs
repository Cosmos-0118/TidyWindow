using System;
using System.Globalization;
using System.Windows.Data;

namespace TidyWindow.App.Converters;

public sealed class WidthBreakpointConverter : IValueConverter
{
    public double Breakpoint { get; set; } = 1024;

    public bool IsLessThan { get; set; } = true;

    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value switch
        {
            double numeric => numeric,
            float f => f,
            int i => i,
            string text when double.TryParse(text, NumberStyles.Float, culture, out var parsed) => parsed,
            _ => double.NaN
        };

        if (double.IsNaN(width))
        {
            return false;
        }

        return IsLessThan ? width <= Breakpoint : width >= Breakpoint;
    }

    public object? ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
