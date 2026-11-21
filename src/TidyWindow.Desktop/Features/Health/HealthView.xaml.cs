using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TidyWindow.Next.Desktop;

public sealed partial class HealthView : UserControl
{
    public HealthView() => InitializeComponent();

    private HealthViewModel? Model => DataContext as HealthViewModel;

    // The card's button and the details view's button both end up here: one check, whichever asked.
    private async void CheckClick(object sender, RoutedEventArgs e) { if (Model is { } model) await model.CheckBatteryAsync(); }
    private void DetailsClick(object sender, RoutedEventArgs e) => Model?.OpenDetails();
    private void BackClick(object sender, RoutedEventArgs e) => Model?.ShowDevices();
}
