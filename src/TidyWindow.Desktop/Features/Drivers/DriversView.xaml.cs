using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

public sealed partial class DriversView : UserControl
{
    public DriversView() => InitializeComponent();

    private DriversViewModel? Model => DataContext as DriversViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    private void StopClick(object sender, RoutedEventArgs e) => Model?.Stop();
    private async void SettingsClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.OpenOptionalUpdatesAsync(); }
    private void DetailsClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is DriverRow row) row.ToggleDetails(); }

    // Clicking anywhere on a row opens it, except on the controls that do their own thing.
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DriverRow row } line) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, line); node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        row.ToggleDetails();
    }

    // The confirmation lives in the view model, which is given the window's one-dialog-at-a-time question; the click only hands over the row.
    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DriverRow row } && Model is { } model) await model.InstallAsync(row);
    }

    private async void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DriverHistoryRow row } && Model is { } model) await model.RestoreAsync(row);
    }

    private async void ToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DriverMakerRow row } && Model is { } model) await model.OpenToolAsync(row);
    }

    private void HubClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DriverMakerRow row }) Model?.FindToolInInstallHub(row);
    }
}
