using System;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class LatencyGuardPage : Page
{
    private readonly LatencyGuardViewModel _viewModel;

    public LatencyGuardPage(LatencyGuardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
    }
}
