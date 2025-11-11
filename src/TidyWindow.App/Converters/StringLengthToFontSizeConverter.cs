using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TidyWindow.App.Converters;

/// <summary>
/// Picks a font size based on the length of the provided text so long names shrink gracefully.
/// </summary>
public sealed class StringLengthToFontSizeConverter : IValueConverter
{
    public double LargeSize { get; set; } = 16;

    public double MediumSize { get; set; } = 14;

    public double SmallSize { get; set; } = 12;

    public double MinimumSize { get; set; } = 11;

    public int MediumLengthThreshold { get; set; } = 40;

    public int SmallLengthThreshold { get; set; } = 80;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var scale = CalculateScale(parameter);
        var largeSize = EnsureMinimum(Math.Round(LargeSize * scale, 1));
        var mediumSize = EnsureMinimum(Math.Round(MediumSize * scale, 1));
        var smallSize = EnsureMinimum(Math.Round(SmallSize * scale, 1));

        var mediumThreshold = MediumLengthThreshold;
        var smallThreshold = SmallLengthThreshold;

        if (scale < 1)
        {
            mediumThreshold = Math.Max(24, (int)Math.Round(mediumThreshold * scale));
            smallThreshold = Math.Max(mediumThreshold + 8, (int)Math.Round(smallThreshold * scale));
        }

        if (value is string text)
        {
            var length = text.Length;
            if (length <= mediumThreshold)
            {
                return largeSize;
            }

            if (length <= smallThreshold)
            {
                return mediumSize;
            }

            return smallSize;
        }

        return largeSize;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }

    private double CalculateScale(object parameter)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var scale = screenWidth switch
        {
            < 1366 => 0.82,
            < 1600 => 0.9,
            < 1920 => 0.95,
            _ => 1.0
        };

        if (parameter is string hint && hint.Equals("compact", StringComparison.OrdinalIgnoreCase))
        {
            scale *= 0.95;
        }

        return Math.Clamp(scale, 0.7, 1.0);
    }

    private double EnsureMinimum(double value) => value < MinimumSize ? MinimumSize : value;
}
