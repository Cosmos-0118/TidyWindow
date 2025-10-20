using System;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Views;

public partial class PackageMaintenancePage : Page
{
    private readonly PackageMaintenanceViewModel _viewModel;
    private bool _disposed;

    public PackageMaintenancePage(PackageMaintenanceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        Unloaded += OnPageUnloaded;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;

        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            await asyncCommand.ExecuteAsync(null);
        }
        else
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private bool ConfirmElevation(string message)
    {
        var prompt = string.IsNullOrWhiteSpace(message)
            ? "This operation requires administrator privileges. Restart as administrator to continue?"
            : message;

        var result = MessageBox.Show(prompt,
            "Administrator privileges needed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        return result == MessageBoxResult.Yes;
    }

    private void OnAdministratorRestartRequested(object? sender, EventArgs e)
    {
        MessageBox.Show(
            "TidyWindow will relaunch with administrator privileges. Close this window if it does not exit automatically.",
            "Restarting as administrator",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        WpfApplication.Current?.Shutdown();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.AdministratorRestartRequested -= OnAdministratorRestartRequested;
        _viewModel.ConfirmElevation = null;
        _viewModel.Dispose();
        Unloaded -= OnPageUnloaded;
        _disposed = true;
    }
}
