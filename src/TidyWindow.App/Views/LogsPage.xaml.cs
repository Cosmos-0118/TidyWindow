using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class LogsPage : Page
{
    private readonly LogsViewModel _viewModel;
    private bool _isDisposed;
    private readonly bool _shouldDisposeOnUnload;

    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _shouldDisposeOnUnload = !PageCacheRegistry.IsCacheable(GetType());
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed || !_shouldDisposeOnUnload)
        {
            return;
        }

        Unloaded -= OnUnloaded;
        _viewModel.Dispose();
        _isDisposed = true;
    }

}
