using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

public sealed partial class FixesView : UserControl
{
    public FixesView() => InitializeComponent();

    private FixesViewModel? Model => DataContext as FixesViewModel;

    private void QueueClick(object sender, RoutedEventArgs e) => Model?.OpenQueue();
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }
    private void DetailsClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is FixRow row) row.ToggleDetails(); }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<FixesTab>(tag, out var next)) model.Tab = next;
    }

    // Clicking anywhere on a row opens it, except on the controls that do their own thing.
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FixRow row } line) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, line); node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        row.ToggleDetails();
    }

    private async void RunClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FixRow row } || Model is not { } model || row.Busy) return;
        if (FixesViewModel.Warning(row) is { } warning)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Run {row.Name}?",
                Content = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Run",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ActualTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        model.Run(row);
    }
}
