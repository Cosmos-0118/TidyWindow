using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class RuntimeUpdatesPage : Page
{
    private readonly RuntimeUpdatesViewModel _viewModel;

    public RuntimeUpdatesPage(RuntimeUpdatesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_viewModel.RefreshCommand is IAsyncRelayCommand asyncCommand)
        {
            asyncCommand.ExecuteAsync(null);
        }
        else
        {
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    private void Hyperlink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri?.AbsoluteUri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Best effort: swallow exceptions so the UI remains responsive.
        }

        e.Handled = true;
    }
}
