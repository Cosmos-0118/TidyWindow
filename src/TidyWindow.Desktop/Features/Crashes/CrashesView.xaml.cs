using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed partial class CrashesView : UserControl
{
    public CrashesView() => InitializeComponent();

    private CrashesViewModel? Model => DataContext as CrashesViewModel;

    private async void RefreshClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RefreshAsync(); }
    private async void ReadAgainClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ReadEverythingAgainAsync(); }
    private void QueryChanged(object sender, TextChangedEventArgs e) { if (Model is { } model) model.Query = ((TextBox)sender).Text; }

    private void EventViewerClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* Event Viewer is not available on every edition, and there is nothing to add */ }
    }

    private async void ClearClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear saved crash history?",
            Content = new TextBlock { Text = "This forgets what TidyWindow saved, including crashes Windows has since removed from its own log. Windows’ records are not touched, and whatever it still has is read in again now.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme
        };
        if (await dialog.ShowGatedAsync() == ContentDialogResult.Primary) await model.ClearHistoryAsync();
    }

    private async void CopyClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var ok = TryCopy(model.Report());
        CopyLabel.Text = ok ? "Copied" : "Clipboard busy";
        CopyIcon.Glyph = ok ? "" : "";
        await Task.Delay(1500);
        CopyLabel.Text = "Copy";
        CopyIcon.Glyph = "";
    }

    private static bool TryCopy(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return false; }
    }

    private void TabChecked(object sender, RoutedEventArgs e) { if (Model is { } model && TagOf(sender) is { } tag && Enum.TryParse<CrashTab>(tag, out var next)) model.Tab = next; }
    private void RangeChecked(object sender, RoutedEventArgs e) { if (Model is { } model && TagOf(sender) is { } tag && Enum.TryParse<CrashRange>(tag, out var next)) model.Range = next; }
    private void FilterChecked(object sender, RoutedEventArgs e) { if (Model is { } model && TagOf(sender) is { } tag && Enum.TryParse<CrashFilter>(tag, out var next)) model.Filter = next; }
    private void SortChecked(object sender, RoutedEventArgs e) { if (Model is { } model && TagOf(sender) is { } tag && Enum.TryParse<CrashSort>(tag, out var next)) model.Sort = next; }
    private static string? TagOf(object sender) => (sender as RadioButton)?.Tag as string;

    private static T? Row<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void TechnicalClick(object sender, RoutedEventArgs e) => Row<CrashEventRow>(sender)?.ToggleTechnical();
    private void DismissInsightClick(object sender, RoutedEventArgs e) { if (Row<CrashInsight>(sender) is { } insight) Model?.DismissInsight(insight); }
    private void AppDetailsClick(object sender, RoutedEventArgs e) => Row<CrashAppRow>(sender)?.Toggle();
    private void EventDetailsClick(object sender, RoutedEventArgs e) => Row<CrashEventRow>(sender)?.Toggle();
    private void ShowAllClick(object sender, RoutedEventArgs e) => Row<CrashAppRow>(sender)?.ShowAll();
    private void AppTapped(object sender, TappedRoutedEventArgs e) { if (Row<CrashAppRow>(sender) is { } row && !FromControl(sender, e)) row.Toggle(); }
    private void EventTapped(object sender, TappedRoutedEventArgs e) { if (Row<CrashEventRow>(sender) is { } row && !FromControl(sender, e)) row.Toggle(); }

    // Clicking anywhere on a row opens it, except on the buttons, which do their own thing.
    private static bool FromControl(object sender, TappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, sender); node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return true;
        return false;
    }

    private async void CopyEventClick(object sender, RoutedEventArgs e)
    {
        if (Row<CrashEventRow>(sender) is not { } row || !TryCopy(CrashReport.Format([row.Event]))) return;
        row.SetCopied(true);
        await Task.Delay(1500);
        row.SetCopied(false);
    }

    private async void CopyAppClick(object sender, RoutedEventArgs e)
    {
        if (Row<CrashAppRow>(sender) is not { } row || !TryCopy(CrashReport.Format(row.App))) return;
        row.SetCopied(true);
        await Task.Delay(1500);
        row.SetCopied(false);
    }
    private void SearchWebClick(object sender, RoutedEventArgs e) { if (Row<CrashEventRow>(sender) is { } row) Model?.SearchWeb(row.Event); }
    private void OpenReportClick(object sender, RoutedEventArgs e) { if (Row<CrashEventRow>(sender) is { } row) Model?.OpenReport(row.Event); }
    private void ShowProgramClick(object sender, RoutedEventArgs e) { if (Row<CrashEventRow>(sender) is { } row) Model?.ShowFile(row.Event.AppPath, "That program is no longer where it was"); }
    private void ShowAppFolderClick(object sender, RoutedEventArgs e) { if (Row<CrashAppRow>(sender) is { } row) Model?.ShowFile(row.Path, "That program is no longer where it was"); }
}
