using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed partial class LabView : UserControl
{
    private LabViewModel? watched;

    public LabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch();
    }

    private LabViewModel? Model => DataContext as LabViewModel;

    // The card height lives on the layout, which cannot bind to the view model, so it follows the model from here.
    private void Watch()
    {
        if (watched is not null) watched.PropertyChanged -= ModelChanged;
        watched = Model;
        if (watched is null) return;
        watched.PropertyChanged += ModelChanged;
        MetricsLayout.MinItemHeight = watched.CardHeight;
    }

    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LabViewModel.CardHeight) && watched is not null) MetricsLayout.MinItemHeight = watched.CardHeight;
    }

    private async void RunClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RunCheckAsync(); }
    private void StopClick(object sender, RoutedEventArgs e) => Model?.StopCheck();
    private async void ClearHistoryClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ClearHistoryAsync(); }
    private async void PowerClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.SwitchPowerAsync(); }
    private void TuneClick(object sender, RoutedEventArgs e) => Model?.OpenTune();
    private async void RefreshClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RefreshAppsAsync(); }
    private async void PutBackClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.PutBackAllAsync(); }

    private void HistoryToggleClick(object sender, RoutedEventArgs e) => Model?.ToggleHistory();

    private void CompareClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckHistoryRow row }) Model?.CompareWith(row.Run);
    }

    // Also fires when the page itself fills or empties the list, which has nothing added or is the choice already in force, and both are ignored.
    private void BaselineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is BaselineChoice choice) Model?.CompareWith(choice.Run);
    }

    private async void EaseClick(object sender, RoutedEventArgs e) => await Choose(sender, BoostLevel.Ease);
    private async void NormalClick(object sender, RoutedEventArgs e) => await Choose(sender, BoostLevel.Normal);
    private async void BoostClick(object sender, RoutedEventArgs e) => await Choose(sender, BoostLevel.Boost);

    private async Task Choose(object sender, BoostLevel level)
    {
        if (sender is FrameworkElement { DataContext: AppRow row } && Model is { } model) await model.SetAppAsync(row, level);
    }
}
