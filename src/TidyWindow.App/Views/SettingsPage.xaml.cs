using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class SettingsPage : Page, INavigationAware
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        if (viewModel is null)
        {
            throw new ArgumentNullException(nameof(viewModel));
        }

        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (DataContext is SettingsViewModel viewModel
            && viewModel.CheckForUpdatesCommand is IAsyncRelayCommand command
            && !viewModel.HasAttemptedUpdateCheck)
        {
            try
            {
                await command.ExecuteAsync(null);
            }
            catch
            {
                // Silent failure – manual checks remain available.
            }
        }
    }

    private void UpdateLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        var target = e.Uri?.ToString();
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Navigation failures are non-fatal; ignore.
        }
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        // No special handling needed for settings page
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        // No special handling needed for settings page
    }
}
