using System.Windows.Controls;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class ProjectOblivionFlowPage : Page, ICacheablePage
{
    public ProjectOblivionFlowPage(ProjectOblivionPopupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
