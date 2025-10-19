using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Views;

public partial class TasksPage : Page
{
    public TasksPage(TasksViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
