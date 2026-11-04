using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace TidyWindow.Next.Desktop;

internal sealed partial class AppearanceSettings
{
    /// <summary>
    /// The theme a window should wear: the one picked here, or the one Windows is using when none was picked. When Windows will not say
    /// which that is, <see cref="ElementTheme.Default"/> lets the window follow it on its own.
    /// </summary>
    public static ElementTheme ThemeFor(Appearance appearance, UISettings? systemColors)
    {
        if (appearance == Appearance.Light) return ElementTheme.Light;
        if (appearance == Appearance.Dark) return ElementTheme.Dark;
        if (systemColors is null) return ElementTheme.Default;
        try
        {
            var background = systemColors.GetColorValue(UIColorType.Background);
            return background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 128 ? ElementTheme.Light : ElementTheme.Dark;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return ElementTheme.Default; }
    }
}
