using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class ProjectOblivionFlowPage : Page
{
    public ProjectOblivionFlowPage(ProjectOblivionPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
