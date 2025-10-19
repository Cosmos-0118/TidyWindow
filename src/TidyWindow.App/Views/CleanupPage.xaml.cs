using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class CleanupPage : Page
{
    public CleanupPage(CleanupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
