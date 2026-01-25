using System;

namespace TidyWindow.App.Helpers;

public static class ByteSizeFormatter
{
    /// <summary>
    /// Formats a byte count into a human-friendly string using a base of 1024 with up to one decimal place.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.#} {units[unitIndex]}";
    }
}
