using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace TidyWindow.Next.Desktop;

public sealed partial class StartupView : UserControl
{
    public StartupView() => InitializeComponent();

    private StartupViewModel? Model => DataContext as StartupViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    private async void RestoreAllClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RestoreAllAsync(); }
    private async void TurnBackOnClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is ChangeRow row) await model.TurnBackOnAsync(row); }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }
    private void ShowFolderClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is StartupRow row) StartupViewModel.ShowInFolder(row); }
    private void DetailsClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is StartupRow row) row.ToggleDetails(); }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<StartupTab>(tag, out var next)) model.Tab = next;
    }

    private void FilterChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<StartupFilter>(tag, out var next)) model.Filter = next;
    }

    // Clicking anywhere on a row opens it, except on the controls that do their own thing.
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StartupRow row } line) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, line); node = VisualTreeHelper.GetParent(node))
            if (node is ToggleSwitch or ButtonBase) return;
        row.ToggleDetails();
    }

    // The switch follows the real state: it only stays where the person put it once Windows has accepted the change.
    private async void ToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not StartupRow row || Model is not { } model) return;
        var wanted = toggle.IsOn;
        if (wanted == row.Enabled) return;
        if (!wanted && StartupViewModel.Warning(row) is { } warning)
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
