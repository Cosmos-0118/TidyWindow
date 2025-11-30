using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class EssentialsPage : Page
{
    private readonly EssentialsViewModel _viewModel;
    private bool _disposed;
    private readonly bool _shouldDisposeOnUnload;

    public EssentialsPage(EssentialsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        _shouldDisposeOnUnload = !PageCacheRegistry.IsCacheable(GetType());
        Unloaded += OnPageUnloaded;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_shouldDisposeOnUnload)
        {
            return;
        }

        Unloaded -= OnPageUnloaded;
        _viewModel.Dispose();
        _disposed = true;
    }

}
