using System;
using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class InstallHubCatalogView : UserControl
{
    private const double CardWidth = 320d;
    private const double CardHeight = 230d;
    private const double CardSpacing = 18d;
    private const double HostPadding = 200d;
    private const double VerticalAllowance = 320d;

    public InstallHubCatalogView()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePageSize(ActualWidth, ActualHeight);
        DataContextChanged += (_, _) => UpdatePageSize(ActualWidth, ActualHeight);
    }

    private void OnCatalogHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void UpdatePageSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(CardWidth, width - HostPadding);
        var availableHeight = Math.Max(CardHeight * 2, height - VerticalAllowance);

        var columns = Math.Max(1, (int)Math.Floor(availableWidth / (CardWidth + CardSpacing)));
        var rows = Math.Max(2, (int)Math.Floor(availableHeight / (CardHeight + CardSpacing)));
        var pageSize = Math.Max(6, columns * rows);

        if (DataContext is InstallHubViewModel viewModel && viewModel.CatalogPageSize != pageSize)
        {
            viewModel.CatalogPageSize = pageSize;
        }
    }
}
