using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed partial class LabView : UserControl
{
    public LabView() => InitializeComponent();

    private LabViewModel? Model => DataContext as LabViewModel;

    private async void RunClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RunCheckAsync(); }
    private void StopClick(object sender, RoutedEventArgs e) => Model?.StopCheck();
    private async void ClearHistoryClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.ClearHistoryAsync(); }
    private async void PowerClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.SwitchPowerAsync(); }
    private void TuneClick(object sender, RoutedEventArgs e) => Model?.OpenTune();
    private async void RefreshClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.RefreshAppsAsync(); }
    private async void PutBackClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.PutBackAllAsync(); }

    private async void EaseClick(object sender, RoutedEventArgs e) => await Choose(sender, BoostLevel.Ease);
    private async void NormalClick(object sender, RoutedEventArgs e) => await Choose(sender, BoostLevel.Normal);
    private async void BoostClick(object sender, RoutedEventArgs e) => await Choose(sender, BoostLevel.Boost);

    private async Task Choose(object sender, BoostLevel level)
    {
        if (sender is FrameworkElement { DataContext: AppRow row } && Model is { } model) await model.SetAppAsync(row, level);
    }
}
