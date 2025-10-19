using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class InstallHubPage : Page
{
    private readonly InstallHubViewModel _viewModel;
    private bool _disposed;

    public InstallHubPage(InstallHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Unloaded -= OnPageUnloaded;
        _viewModel.Dispose();
        _disposed = true;
    }
}
