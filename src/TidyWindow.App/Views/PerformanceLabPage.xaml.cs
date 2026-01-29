using System.Windows;
using System.Windows.Controls;
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
}
