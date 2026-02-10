using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Views;

public partial class CleanupPage : Page, INavigationAware
{
    private readonly CleanupViewModel _viewModel;
    private bool _disposed;

    public CleanupPage(CleanupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += CleanupPage_Unloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private bool ConfirmElevation(string message)
    {
        var warning = string.IsNullOrWhiteSpace(message)
            ? "These items may be protected. Restart as administrator to continue?"
            : message;

        var result = System.Windows.MessageBox.Show(warning, "Administrator privileges needed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes;
    }

    private void OnAdministratorRestartRequested(object? sender, EventArgs e)
    {
        var app = WpfApplication.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }

    private void CleanupPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.AdministratorRestartRequested -= OnAdministratorRestartRequested;
        _viewModel.ConfirmElevation = null;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.IsFilesPopupVisible = false;
        Unloaded -= CleanupPage_Unloaded;
        _disposed = true;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _disposed)
        {
            RestoreViewModelBindings();
        }
    }

    private void RestoreViewModelBindings()
    {
        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += CleanupPage_Unloaded;
        _disposed = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(CleanupViewModel.IsCelebrationPhase), StringComparison.Ordinal))
        {
            return;
        }

        if (!_viewModel.IsCelebrationPhase)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => CelebrationView?.RestartAnimation()));
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        if (_disposed)
        {
            RestoreViewModelBindings();
        }
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        // No special handling needed
    }

    private void OnFilesDialogOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Grid grid && grid.Background != null)
        {
            _viewModel.IsFilesPopupVisible = false;
            e.Handled = true;
        }
    }
}
