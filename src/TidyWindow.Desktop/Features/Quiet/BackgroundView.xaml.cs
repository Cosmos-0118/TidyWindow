using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

public sealed partial class BackgroundView : UserControl
{
    public BackgroundView() => InitializeComponent();

    private BackgroundViewModel? Model => DataContext as BackgroundViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    private async void TurnOffAgainClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.TurnOffAgainAsync(); }

    // Shows exactly what will be switched off before anything is, and never includes an item marked Careful.
    private async void TurnOffSafeClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var rows = model.SafeRows;
        if (rows.Count == 0) return;
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(new TextBlock { Text = "These are safe to turn off: nothing you are likely to use depends on them. Items marked Careful are not included, and every change can be undone from the Turned off tab.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var row in rows) list.Children.Add(new TextBlock { Text = "•  " + row.Name, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = rows.Count == 1 ? "Turn off 1 item?" : $"Turn off {rows.Count} items?",
            Content = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Turn off",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await model.TurnOffSafeAsync(rows);
    }

    private async void RestoreAllClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RestoreAllAsync(); }
    private async void TurnBackOnClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is BackgroundChangeRow row) await model.TurnBackOnAsync(row); }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }
    private void DetailsClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is BackgroundRow row) row.ToggleDetails(); }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<BackgroundTab>(tag, out var next)) model.Tab = next;
    }

    private void FilterChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<BackgroundFilter>(tag, out var next)) model.Filter = next;
    }

    // Clicking anywhere on a row opens it, except on the controls that do their own thing.
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BackgroundRow row } line) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, line); node = VisualTreeHelper.GetParent(node))
            if (node is ToggleSwitch or ButtonBase) return;
        row.ToggleDetails();
    }

    // The switch follows the real state: it only stays where the person put it once Windows has accepted the change.
    private async void ToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not BackgroundRow row || Model is not { } model) return;
        var wanted = toggle.IsOn;
        if (wanted == row.Enabled) return;
        if (!wanted && BackgroundViewModel.Warning(row) is { } warning)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Turn off {row.Name}?",
                Content = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Turn off",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ActualTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { toggle.IsOn = row.Enabled; return; }
        }
        if (!await model.SetAsync(row, wanted)) toggle.IsOn = row.Enabled;
    }
}
