using System.Windows;
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

    private void ProjectOblivionPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectOblivionViewModel viewModel)
        {
            viewModel.ResumeActiveFlowIfNeeded();
        }
    }
}
