using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

/// <summary>Keeps the column titles above a list lined up with the columns in its rows. A list's scroll lane takes 12 pixels from the rows when it is showing and the titles do not know that, so the titles move in by the same amount.</summary>
public static class ListLane
{
    private const double Lane = 12;

    public static void Attach(ListViewBase list, FrameworkElement header)
    {
        list.ApplyTemplate();
        var scroller = Find(list);
        if (scroller is null) return;
        void Apply() => header.Margin = new Thickness(0, 0, scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible ? Lane : 0, 0);
        scroller.RegisterPropertyChangedCallback(ScrollViewer.ComputedVerticalScrollBarVisibilityProperty, (_, _) => Apply());
        Apply();
    }

    private static ScrollViewer? Find(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer found) return found;
            if (Find(child) is { } inner) return inner;
        }
        return null;
    }
}
