// Just enough of WinUI for the shell files linked into this project to compile. Nothing here draws: the tests only read the values the
// view models hand to the window, such as which rows are visible and which toasts are closing.
namespace Windows.UI
{
    public readonly record struct Color(byte A, byte R, byte G, byte B);
}

namespace Microsoft.UI
{
    public static class ColorHelper { public static Windows.UI.Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b); }
    public static class Colors { public static Windows.UI.Color Transparent => default; }
}

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}

namespace Microsoft.UI.Xaml.Media
{
    public abstract class Brush { }
    public sealed class SolidColorBrush(Windows.UI.Color color) : Brush { public Windows.UI.Color Color { get; } = color; }
}
