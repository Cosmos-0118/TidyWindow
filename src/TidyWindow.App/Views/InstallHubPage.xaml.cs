using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class InstallHubPage : Page
{
    private readonly InstallHubViewModel _viewModel;
    private bool _disposed;
    private readonly bool _shouldDisposeOnUnload;

    public InstallHubPage(InstallHubViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        _shouldDisposeOnUnload = !PageCacheRegistry.IsCacheable(GetType());

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        await EnsureViewModelInitializedAsync();
    }

    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            await EnsureViewModelInitializedAsync();
        }
    }

    private async Task EnsureViewModelInitializedAsync()
    {
        if (!_viewModel.IsInitialized)
        {
            await _viewModel.EnsureLoadedAsync();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_shouldDisposeOnUnload)
        {
            return;
        }

        IsVisibleChanged -= OnIsVisibleChanged;
        Unloaded -= OnPageUnloaded;
        _viewModel.Dispose();
        _disposed = true;
    }
}
