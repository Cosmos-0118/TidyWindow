using System;
using System.Windows.Controls;
using System.Windows.Navigation;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class DriverUpdatesPage : Page
{
    private readonly DriverUpdatesViewModel _viewModel;
    private bool _disposed;

    public DriverUpdatesPage(DriverUpdatesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel.HasScanned)
        {
            return;
        }

        await _viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _disposed = true;
    }

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // No-op: navigation failures are not fatal.
        }

        e.Handled = true;
    }
}
