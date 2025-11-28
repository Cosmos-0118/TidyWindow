using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class AntiSystemPage : Page
{
    private readonly AntiSystemViewModel _viewModel;

    public AntiSystemPage(AntiSystemViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.EnsureInitialized();
    }
}
