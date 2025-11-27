using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TidyWindow.App.Services;
using TidyWindow.App.ViewModels;
using Point = System.Windows.Point;

namespace TidyWindow.App.Views;

public partial class ProjectOblivionFlowPage : Page, ICacheablePage
{
    private readonly ProjectOblivionPopupViewModel _viewModel;

    public ProjectOblivionFlowPage(ProjectOblivionPopupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ScrollActiveStageIntoView();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectOblivionPopupViewModel.ActiveStage))
        {
            ScrollActiveStageIntoView();
        }
    }

    private void ScrollActiveStageIntoView()
    {
        if (!IsLoaded || FlowScrollViewer is null || StageItemsControl is null)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            var targetStage = _viewModel.Timeline.FirstOrDefault(stage => stage.Stage == _viewModel.ActiveStage);
            if (targetStage is null)
            {
                return;
            }

            var container = StageItemsControl.ItemContainerGenerator.ContainerFromItem(targetStage) as FrameworkElement;
            if (container is null)
            {
                StageItemsControl.UpdateLayout();
                container = StageItemsControl.ItemContainerGenerator.ContainerFromItem(targetStage) as FrameworkElement;
            }

            if (container is null || FlowScrollViewer.ViewportHeight <= 0)
            {
                return;
            }

            FlowScrollViewer.UpdateLayout();

            var transform = container.TransformToAncestor(FlowScrollViewer);
            var elementOrigin = transform.Transform(new Point(0, 0));
            var centeredOffset = elementOrigin.Y + container.ActualHeight / 2 - FlowScrollViewer.ViewportHeight / 2;
            FlowScrollViewer.ScrollToVerticalOffset(Math.Max(0, centeredOffset));
        }, DispatcherPriority.Background);
    }
}
