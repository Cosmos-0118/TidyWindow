using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class ProjectOblivionPage : Page
{
    public ProjectOblivionPage(ProjectOblivionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
