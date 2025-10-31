using System;
using System.Globalization;
using System.Windows.Data;

namespace TidyWindow.App.Converters;

/// <summary>
/// Adjusts the navigation rail width so it scales with the window while staying within sensible bounds.
/// </summary>
public sealed class NavigationWidthConverter : IValueConverter
{
    /// <summary>
    /// Percentage of the window width to allocate to the navigation rail.
    /// </summary>
    public double Ratio { get; set; } = 0.22;

    /// <summary>
    /// Smallest width the navigation rail should shrink to.
    /// </summary>
    public double MinWidth { get; set; } = 220;

    /// <summary>
    /// Largest width the navigation rail should grow to.
    /// </summary>
    public double MaxWidth { get; set; } = 340;

    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var windowWidth = value switch
        {
            double numeric => numeric,
            float f => f,
            int i => i,
            string text when double.TryParse(text, NumberStyles.Float, culture, out var parsed) => parsed,
            _ => double.NaN
        };

        if (double.IsNaN(windowWidth) || double.IsInfinity(windowWidth) || windowWidth <= 0)
        {
            return MinWidth;
        }

        var target = windowWidth * Ratio;
        var clamped = Math.Clamp(target, MinWidth, Math.Max(MinWidth, MaxWidth));
        return clamped;
    }

    public object? ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
