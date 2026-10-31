using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

public sealed partial class TuneView : UserControl
{
    public TuneView() => InitializeComponent();

    private TuneViewModel? Model => DataContext as TuneViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    private async void RestoreAllClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RestoreAllAsync(); }
    private async void TurnOnAgainClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.TurnOnAgainAsync(); }
    private async void RestartExplorerClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RestartExplorerAsync(); }
    private async void PutBackClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is TuneChangeRow row) await model.PutBackAsync(row); }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }
    private void DetailsClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is TuneRow row) row.ToggleDetails(); }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<TuneTab>(tag, out var next)) model.Tab = next;
    }

    private void FilterChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<TuneFilter>(tag, out var next)) model.Filter = next;
    }

    // Clicking anywhere on a row opens it, except on the controls that do their own thing.
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TuneRow row } line) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, line); node = VisualTreeHelper.GetParent(node))
            if (node is ToggleSwitch or ButtonBase) return;
        row.ToggleDetails();
    }

    // The switch follows the real state: it only stays where the person put it once Windows has accepted the change.
    private async void ToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not TuneRow row || Model is not { } model) return;
        var wanted = toggle.IsOn;
        if (wanted == row.Enabled) return;
        if (wanted && TuneViewModel.Warning(row) is { } warning)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Turn on {row.Name}?",
                Content = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Turn on",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ActualTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { toggle.IsOn = row.Enabled; return; }
        }
        if (!await model.SetAsync(row, wanted)) toggle.IsOn = row.Enabled;
    }
}
