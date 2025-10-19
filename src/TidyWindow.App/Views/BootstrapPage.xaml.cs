using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class BootstrapPage : Page
{
    public BootstrapPage(BootstrapViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
