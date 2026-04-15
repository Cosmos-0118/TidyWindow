using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class PerformanceLabPage : Page
{
    private readonly PerformanceLabViewModel _viewModel;

    public PerformanceLabPage(PerformanceLabViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _viewModel.ShowStatusAction = message => System.Windows.MessageBox.Show(message, "Current performance status", MessageBoxButton.OK, MessageBoxImage.Information);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    private void OnRestorePointBannerClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ShowRestorePointsDialogCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDialogOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Grid grid && grid.Background != null)
        {
            _viewModel.IsRestorePointsDialogVisible = false;
            e.Handled = true;
        }
    }
}
