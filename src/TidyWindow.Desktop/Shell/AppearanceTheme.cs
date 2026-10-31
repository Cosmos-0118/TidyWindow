using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace TidyWindow.Next.Desktop;

internal sealed partial class AppearanceSettings
{
    /// <summary>The theme a window should wear: the one picked here, or the one Windows is using when none was picked.</summary>
    public static ElementTheme ThemeFor(Appearance appearance, UISettings systemColors)
    {
        var background = systemColors.GetColorValue(UIColorType.Background);
        var systemLight = background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 128;
        return appearance == Appearance.Light || appearance == Appearance.System && systemLight ? ElementTheme.Light : ElementTheme.Dark;
    }
}
