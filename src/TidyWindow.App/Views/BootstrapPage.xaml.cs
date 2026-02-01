using System.Windows.Controls;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class BootstrapPage : Page, INavigationAware
{
    public BootstrapPage(BootstrapViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        // No special handling needed for bootstrap page
    }

    /// <inheritdoc />
    public void OnNavigatingFrom()
    {
        // No special handling needed for bootstrap page
    }
}
