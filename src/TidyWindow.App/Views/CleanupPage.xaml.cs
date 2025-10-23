using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;
using MessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Views;

public partial class CleanupPage : Page
{
    private readonly CleanupViewModel _viewModel;
    private bool _disposed;

    public CleanupPage(CleanupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.ConfirmDeletion = ConfirmDeletion;
        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        Unloaded += CleanupPage_Unloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private bool ConfirmDeletion(CleanupDeletionConfirmation context)
    {
        var sizeText = context.TotalSizeMegabytes > 0
            ? $"{context.TotalSizeMegabytes:F2} MB"
            : "0 MB";

        var message = $"You're about to permanently delete {context.ItemCount:N0} item(s) totaling {sizeText}.\n\n" +
                      "This action cannot be undone. Do you want to continue?";

        var result = MessageBox.Show(message, "Confirm permanent deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private bool ConfirmElevation(string message)
    {
        var warning = string.IsNullOrWhiteSpace(message)
            ? "These items may be protected. Restart as administrator to continue?"
            : message;

        var result = MessageBox.Show(warning, "Administrator privileges needed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
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
        _viewModel.ConfirmDeletion = null;
        _viewModel.ConfirmElevation = null;
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
        _viewModel.ConfirmDeletion = ConfirmDeletion;
        _viewModel.ConfirmElevation = ConfirmElevation;
        _viewModel.AdministratorRestartRequested += OnAdministratorRestartRequested;
        Unloaded += CleanupPage_Unloaded;
        _disposed = false;
    }
}
