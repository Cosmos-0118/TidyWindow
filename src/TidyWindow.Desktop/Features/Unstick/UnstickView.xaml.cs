using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

public sealed partial class UnstickView : UserControl
{
    public UnstickView() => InitializeComponent();

    private UnstickViewModel? Model => DataContext as UnstickViewModel;

    private async void RunClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UnstickRow row } && Model is { } model) await model.RunAsync(row);
    }

    private void DetailsClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is UnstickRow row) row.ToggleDetails(); }

    // Clicking anywhere on a row opens it, except on the controls that do their own thing.
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UnstickRow row } line) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, line); node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        row.ToggleDetails();
    }
}
