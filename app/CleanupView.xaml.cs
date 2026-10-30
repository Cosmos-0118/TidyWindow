using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

public sealed partial class CleanupView : UserControl
{
    public CleanupView() => InitializeComponent();

    private CleanupViewModel? Model => DataContext as CleanupViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    private void DismissClick(object sender, RoutedEventArgs e) => Model?.DismissResult();
    private async void UndoClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.UndoAsync(); }

    private async void CleanClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        // Plain caches clean in one click; only items that cannot be undone ask first.
        var warning = model.ConfirmText;
        if (warning.Length > 0 && XamlRoot is not null)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = model.CleanLabel + "?", Content = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Clean", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await model.CleanAsync();
    }
}
