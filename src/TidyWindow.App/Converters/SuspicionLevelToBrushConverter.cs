using System;
using System.Globalization;
using System.Windows.Data;
using Media = System.Windows.Media;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.Converters;

/// <summary>
/// Maps <see cref="SuspicionLevel"/> values to semantic brushes for the Threat Watch tab.
/// </summary>
public sealed class SuspicionLevelToBrushConverter : IValueConverter
{
    public Media.Brush RedBrush { get; set; } = new Media.SolidColorBrush(Media.Color.FromRgb(248, 113, 113));

    public Media.Brush OrangeBrush { get; set; } = new Media.SolidColorBrush(Media.Color.FromRgb(251, 146, 60));

    public Media.Brush YellowBrush { get; set; } = new Media.SolidColorBrush(Media.Color.FromRgb(251, 191, 36));

    public Media.Brush GreenBrush { get; set; } = new Media.SolidColorBrush(Media.Color.FromRgb(52, 211, 153));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SuspicionLevel level)
        {
            return GreenBrush;
        }

        return level switch
        {
            SuspicionLevel.Red => RedBrush,
            SuspicionLevel.Orange => OrangeBrush,
            SuspicionLevel.Yellow => YellowBrush,
            _ => GreenBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
