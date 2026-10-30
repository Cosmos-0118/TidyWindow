using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

/// <summary>Shared solid brushes. Creating a new brush for every binding read adds allocation and layout work on every theme or page change.</summary>
internal static class BrushCache
{
    private static readonly Dictionary<int, SolidColorBrush> brushes = [];
    public static SolidColorBrush Rgb(int rgb)
    {
        if (!brushes.TryGetValue(rgb, out var brush))
            brushes[rgb] = brush = new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        return brush;
    }
    public static SolidColorBrush Transparent { get; } = new(Colors.Transparent);
}
