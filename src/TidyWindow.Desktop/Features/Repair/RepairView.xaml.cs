using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

public sealed partial class RepairView : UserControl
{
    public RepairView() => InitializeComponent();

    private RepairViewModel? Model => DataContext as RepairViewModel;

    private async void ScanClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ScanAsync(); }
    // What is ticked is listed before anything is removed: the recommended items come ticked, and a removal is only undone from the Undo tab.
    private async void FixClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var titles = model.SelectedTitles;
        if (titles.Count == 0) return;
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(new TextBlock { Text = "These records are repaired or removed. Every registry key, shortcut and folder that is removed is kept, so the repair can be undone from the Undo tab.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var title in titles) list.Children.Add(new TextBlock { Text = "•  " + title, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = titles.Count == 1 ? "Fix 1 issue?" : $"Fix {titles.Count} issues?",
            Content = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Fix",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme
        };
        if (await dialog.ShowGatedAsync() == ContentDialogResult.Primary) await model.FixAsync();
    }
    private async void LocateClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.LocateInstallerAsync(); }
    // Shows or hides the list of steps under the button, and turns the arrow to match.
    private void StepsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: StackPanel { Children: [FontIcon arrow, ..] } } button || button.Parent is not StackPanel { Children: [_, Border card] }) return;
        var open = card.Visibility != Visibility.Visible;
        card.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        arrow.Glyph = open ? "" : "";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, open ? "Hide what was tried" : "Show what was tried");
    }

    private void ClearFoldersClick(object sender, RoutedEventArgs e) => Model?.ClearInstallerFolders();
    private async void RepairProgramClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is ProgramRow row) await model.RepairProgramAsync(row); }
    private async void UndoRowClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is HistoryRow row) await model.UndoAsync(row); }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (UndoTab is null || QuarantineTab is null) return;
        ProblemsTab.Visibility = ReferenceEquals(sender, ProblemsItem) ? Visibility.Visible : Visibility.Collapsed;
        ProgramsTab.Visibility = ReferenceEquals(sender, ProgramsItem) ? Visibility.Visible : Visibility.Collapsed;
        UndoTab.Visibility = ReferenceEquals(sender, UndoItem) ? Visibility.Visible : Visibility.Collapsed;
        QuarantineTab.Visibility = ReferenceEquals(sender, QuarantineItem) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void KeepRowClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is QuarantineRow row) await model.ResolveAsync(row, keep: true); }

    private async void RemoveRowClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || (sender as FrameworkElement)?.DataContext is not QuarantineRow row) return;
        if (await Confirm($"Remove {row.Name} for good?", $"{row.Original} will be deleted and cannot be put back afterwards.")) await model.ResolveAsync(row, keep: false);
    }

    private async void KeepSelectedClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ResolveSelectedAsync(keep: true); }

    private async void RemoveSelectedClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || model.SelectedCount == 0) return;
        var hidden = model.HiddenSelectedCount;
        var note = hidden > 0 ? $"{hidden:N0} of them {(hidden == 1 ? "is" : "are")} hidden by your search. " : "";
        if (await Confirm($"Remove {model.SelectedCount:N0} for good?", note + "They will be deleted and cannot be put back afterwards.")) await model.ResolveSelectedAsync(keep: false);
    }

    private void ClearSelectionClick(object sender, RoutedEventArgs e) { Model?.ClearSelection(); }
    private void ClearUndoSearchClick(object sender, RoutedEventArgs e) => Model?.UndoState.Clear();
    private void ClearQuarantineSearchClick(object sender, RoutedEventArgs e) => Model?.QuarantineState.Clear();
    // A tap anywhere on a row opens or closes it; the buttons and the tick box inside keep doing their own job.
    private static bool OnControl(Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => e.OriginalSource is DependencyObject source && (FindParent<Microsoft.UI.Xaml.Controls.Primitives.ButtonBase>(source) is not null || FindParent<CheckBox>(source) is not null);

    private void UndoRowTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) { if (!OnControl(e) && (sender as FrameworkElement)?.DataContext is HistoryRow row) row.IsOpen = !row.IsOpen; }
    private void QuarantineRowTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) { if (!OnControl(e) && (sender as FrameworkElement)?.DataContext is QuarantineRow row) row.IsOpen = !row.IsOpen; }
    private void OptionsTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) { if (!OnControl(e) && Model is { } model) model.OptionsOpen = !model.OptionsOpen; }

    private async Task<bool> Confirm(string title, string text)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = text, PrimaryButtonText = "Remove for good", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, RequestedTheme = ActualTheme };
        return await dialog.ShowGatedAsync() == ContentDialogResult.Primary;
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
        if (await dialog.ShowGatedAsync() == ContentDialogResult.Primary) await model.RemoveProgramAsync(row);
    }
}
