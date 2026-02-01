using System;
using System.Globalization;
using System.Windows.Data;

namespace TidyWindow.App.Converters;

/// <summary>
/// Converts a byte count (long) into a human-readable file size string (KB, MB, GB, etc.).
/// </summary>
public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes)
        {
            if (value is int intBytes)
            {
                bytes = intBytes;
            }
            else if (value is double doubleBytes)
            {
                bytes = (long)doubleBytes;
            }
            else
            {
                return "0 B";
            }
        }

        if (bytes == 0)
        {
            return "0 B";
        }

        var unitIndex = 0;
        var size = (double)bytes;

        while (size >= 1024 && unitIndex < SizeUnits.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:N0} {SizeUnits[unitIndex]}"
            : $"{size:N1} {SizeUnits[unitIndex]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("FileSizeConverter does not support ConvertBack.");
    }
}
