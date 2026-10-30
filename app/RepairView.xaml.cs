using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

public sealed partial class RepairView : UserControl
{
    public RepairView() => InitializeComponent();

    private RepairViewModel? Model => DataContext as RepairViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    private async void FixClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.FixAsync(); }
    private async void UndoRowClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is HistoryRow row) await model.UndoAsync(row); }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (UndoTab is null) return;
        ProblemsTab.Visibility = ReferenceEquals(sender, ProblemsItem) ? Visibility.Visible : Visibility.Collapsed;
        ProgramsTab.Visibility = ReferenceEquals(sender, ProgramsItem) ? Visibility.Visible : Visibility.Collapsed;
        UndoTab.Visibility = ReferenceEquals(sender, UndoItem) ? Visibility.Visible : Visibility.Collapsed;
    }

    // The whole row is the target, not only the small box.
    private void RowTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<CheckBox>(source) is not null) return;
        if ((sender as FrameworkElement)?.DataContext is RepairRow { Finding.CanFix: true } row) row.IsSelected = !row.IsSelected;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = child; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (node is T found) return found;
        return null;
    }

    private async void RemoveProgramClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || (sender as FrameworkElement)?.DataContext is not ProgramRow row) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {row.Name} from the program list?",
            Content = "This takes the program off Windows' list, along with Windows Installer's record of it if there is one, so it can be installed again. The program's own files are not deleted, and you can undo this on this page.",
            PrimaryButtonText = "Remove entry",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await model.RemoveProgramAsync(row);
    }
}
