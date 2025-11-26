using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaPoint = System.Windows.Point;

namespace TidyWindow.App.Converters;

public sealed class AlternationIndexToBrushConverter : IValueConverter
{
    private static readonly MediaBrush[] Palette =
    {
        CreateGradient("#7C3AED", "#312E81"),
        CreateGradient("#38BDF8", "#0F172A"),
        CreateGradient("#22C55E", "#064E3B"),
        CreateGradient("#F97316", "#7C2D12"),
        CreateGradient("#A855F7", "#3B0764")
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int alternationIndex && Palette.Length > 0)
        {
            return Palette[alternationIndex % Palette.Length];
        }

        return Palette.Length > 0 ? Palette[0] : MediaBrushes.Transparent;
    }

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return DependencyProperty.UnsetValue;
    }

    private static MediaBrush CreateGradient(string startHex, string endHex)
    {
        var startColor = (MediaColor)MediaColorConverter.ConvertFromString(startHex)!;
        var endColor = (MediaColor)MediaColorConverter.ConvertFromString(endHex)!;

        var gradient = new LinearGradientBrush(startColor, endColor, new MediaPoint(0, 0), new MediaPoint(1, 1))
        {
            Opacity = 0.45
        };
        gradient.Freeze();

        return gradient;
    }
}
