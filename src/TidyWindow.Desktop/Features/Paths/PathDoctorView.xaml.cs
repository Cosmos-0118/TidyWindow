using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace TidyWindow.Next.Desktop;

public sealed partial class PathDoctorView : UserControl
{
    public PathDoctorView() => InitializeComponent();

    private PathDoctorViewModel? Model => DataContext as PathDoctorViewModel;

    private async void RefreshClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.CheckAgainAsync(); }
    private async void UndoClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.UndoAsync(); }
    private async void AddClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.AddFolderAsync(); }

    private async void LookupKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Model is not { } model) return;
        e.Handled = true;
        await model.LookUpAsync();
    }

    private async void UpClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is PathRow row) await model.MoveAsync(row, -1); }
    private async void DownClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is PathRow row) await model.MoveAsync(row, 1); }
    private async void RemoveClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is PathRow row) await model.RemoveAsync(row); }
    private async void AddAllClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.AddAllToolsAsync(); }
    private async void AddToolClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is ToolRow row) await model.AddToolAsync(row); }

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (ToolsTab is null) return;
        ProgramsTab.Visibility = ReferenceEquals(sender, ProgramsItem) ? Visibility.Visible : Visibility.Collapsed;
        FoldersTab.Visibility = ReferenceEquals(sender, FoldersItem) ? Visibility.Visible : Visibility.Collapsed;
        MachineTab.Visibility = ReferenceEquals(sender, MachineItem) ? Visibility.Visible : Visibility.Collapsed;
        ToolsTab.Visibility = ReferenceEquals(sender, ToolsItem) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UnlockClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.UnlockMachineAsync(); }
    private void LockClick(object sender, RoutedEventArgs e) => Model?.LockMachine();
    private async void AddMachineClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.AddMachineFolderAsync(); }
    private async void MachineFixClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.MachineFixAsync(); }
    private async void MachineUndoClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.MachineUndoAsync(); }
    private void ToggleClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is CommandRow row) row.Toggle(); }
    private async void MakeFirstClick(object sender, RoutedEventArgs e) { if (Model is { } model && (sender as FrameworkElement)?.DataContext is CandidateRow row) await model.MakeFirstAsync(row); }
}
