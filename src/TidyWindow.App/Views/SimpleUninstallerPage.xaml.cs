using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class SimpleUninstallerPage : Page, ICacheablePage
{
    private readonly SimpleUninstallerViewModel _viewModel;
    private bool _initialized;

    public SimpleUninstallerPage(SimpleUninstallerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            Loaded -= OnLoaded;
            return;
        }

        _initialized = true;
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SimpleUninstallerPage initialization failed: {ex}");
        }
    }
}
