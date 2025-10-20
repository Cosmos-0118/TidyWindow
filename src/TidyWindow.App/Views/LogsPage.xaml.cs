using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class LogsPage : Page
{
    private readonly LogsViewModel _viewModel;
    private bool _isDisposed;

    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        Unloaded -= OnUnloaded;
        _viewModel.Dispose();
        _isDisposed = true;
    }
}
