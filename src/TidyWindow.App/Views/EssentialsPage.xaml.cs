using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class EssentialsPage : Page
{
    private readonly EssentialsViewModel _viewModel;
    private bool _disposed;

    public EssentialsPage(EssentialsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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
