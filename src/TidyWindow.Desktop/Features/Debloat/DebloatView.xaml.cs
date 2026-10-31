using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed partial class DebloatView : UserControl
{
    public DebloatView() => InitializeComponent();

    private DebloatViewModel? Model => DataContext as DebloatViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }
    private void SuggestClick(object sender, RoutedEventArgs e) => Model?.SelectSuggested();
    private void ClearClick(object sender, RoutedEventArgs e) => Model?.ClearSelection();
    private void DetailsClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is DebloatRow row) row.ToggleDetails(); }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is RadioButton { Tag: string tag } && Enum.TryParse<DebloatTab>(tag, out var next)) model.Tab = next;
    }

    // Clicking anywhere on a row opens it, except on the controls that do their own thing.
    private void RowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DebloatRow row } line) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, line); node = VisualTreeHelper.GetParent(node))
            if (node is CheckBox or ButtonBase) return;
        row.ToggleDetails();
    }

    // Nothing is removed without the person reading what goes and what it costs.
    private async void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var rows = model.Selected();
        if (rows.Count == 0) return;
        var careful = rows.Any(x => x.Entry.Item.Risk == DebloatRisk.Careful);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = rows.Count == 1 ? $"Remove {rows[0].Name}?" : $"Remove {rows.Count:N0} items?",
            Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = DebloatViewModel.Warning(rows), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = careful ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = ActualTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await model.RemoveAsync(rows);
    }

    // Putting everything back installs apps again from the Store, which can take a while, so it is confirmed once.
    private async void PutAllBackClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var count = model.Removed.Count(x => x.Item is not null);
        if (count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = count == 1 ? "Put 1 item back?" : $"Put {count:N0} items back?",
            Content = new TextBlock { Text = "Windows will be allowed to install each of them again, and TidyWindow installs them from the Microsoft Store or WinGet where that is possible. This can take a few minutes. Windows will ask for administrator permission once.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Put back",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await model.PutAllBackAsync();
    }

    private async void PutBackClick(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && (sender as FrameworkElement)?.DataContext is DebloatRemovedRow row) await model.PutBackAsync(row);
    }
}
