using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TidyWindow.Next.Desktop;

/// <summary>Gives a text block a tooltip with its full text, but only while the text is actually cut off.
/// A tooltip repeating text that is already fully visible is noise, and an empty one is a bug.</summary>
public static class TrimTip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(TrimTip), new PropertyMetadata(false, EnabledChanged));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBlock text || e.NewValue is not true) return;
        // Anchor to the text itself rather than following the pointer, so it does not appear at an arbitrary spot over a row.
        ToolTipService.SetPlacement(text, PlacementMode.Bottom);
        text.IsTextTrimmedChanged += (block, _) => Update(block);
        text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (block, _) => Update((TextBlock)block));
        Update(text);
    }

    private static void Update(TextBlock text) =>
        ToolTipService.SetToolTip(text, text.IsTextTrimmed && !string.IsNullOrWhiteSpace(text.Text) ? text.Text : null);
}
