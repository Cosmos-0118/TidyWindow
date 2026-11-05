using System.Numerics;
using Microsoft.UI.Xaml;

namespace TidyWindow.Next.Desktop;

/// <summary>Makes a placeholder ease in (a short fade and a small rise) each time it appears, instead of snapping in.
/// The motion runs on the compositor, so it costs the UI thread nothing, and it is skipped when Windows has animations turned off.</summary>
public static class EmptyState
{
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.RegisterAttached("Animate", typeof(bool), typeof(EmptyState), new PropertyMetadata(false, AnimateChanged));
    public static bool GetAnimate(DependencyObject element) => (bool)element.GetValue(AnimateProperty);
    public static void SetAnimate(DependencyObject element, bool value) => element.SetValue(AnimateProperty, value);

    private static void AnimateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || e.NewValue is not true) return;
        element.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
        element.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) };
        element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (changed, _) => Appear((UIElement)changed));
    }

    private static void Appear(UIElement element)
    {
        if (element.Visibility != Visibility.Visible) return;
        if (!SystemUi.AnimationsEnabled) { element.Opacity = 1; element.Translation = default; return; }
        // Start faded and a little low, then let the transitions carry it to rest on the next tick.
        element.Opacity = 0; element.Translation = new Vector3(0, 10, 0);
        element.DispatcherQueue.TryEnqueue(() => { element.Opacity = 1; element.Translation = default; });
    }
}
