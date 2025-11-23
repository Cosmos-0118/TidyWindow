using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Converters;

public sealed class PathPilotStatusSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DangerBrush = Create(244, 63, 94);
    private static readonly SolidColorBrush WarningBrush = Create(251, 191, 36);
    private static readonly SolidColorBrush SuccessBrush = Create(34, 197, 94);
    private static readonly SolidColorBrush InfoBrush = Create(56, 189, 248);
    private static readonly SolidColorBrush MutedBrush = Create(148, 163, 184);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PathPilotStatusSeverity severity)
        {
            return severity switch
            {
                PathPilotStatusSeverity.Danger => DangerBrush,
                PathPilotStatusSeverity.Warning => WarningBrush,
                PathPilotStatusSeverity.Success => SuccessBrush,
                PathPilotStatusSeverity.Info => InfoBrush,
                _ => MutedBrush
            };
        }

        return MutedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }

    private static SolidColorBrush Create(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }
}
