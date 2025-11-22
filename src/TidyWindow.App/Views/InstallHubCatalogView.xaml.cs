using System;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace TidyWindow.App.Views;

public partial class InstallHubCatalogView : UserControl
{
    private readonly Thickness _stackedFiltersMargin = new(0, 0, 0, 18);
    private readonly Thickness _stackedListMargin = new(0);
    private Thickness _filtersDefaultMargin;
    private Thickness _listDefaultMargin;
    private GridLength _filtersColumnDefaultWidth;
    private GridLength _listColumnDefaultWidth;
    private GridLength _spacerColumnDefaultWidth;
    private bool _isStackLayout;

    private const double StackBreakpoint = 960d;
    private const double CompactBreakpoint = 1240d;

    public InstallHubCatalogView()
    {
        InitializeComponent();

        _filtersDefaultMargin = FiltersCard.Margin;
        _listDefaultMargin = CatalogListCard.Margin;
        _filtersColumnDefaultWidth = FiltersColumnDefinition.Width;
        _listColumnDefaultWidth = CatalogListColumnDefinition.Width;
        _spacerColumnDefaultWidth = CatalogSpacerColumnDefinition.Width;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateResponsiveLayout(ActualWidth);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }
    }

    // Mirrors the Essentials page breakpoint logic so filters collapse cleanly on smaller displays.
    private void UpdateResponsiveLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }

        var stackLayout = width < StackBreakpoint;
        var compactLayout = width < CompactBreakpoint;

        if (stackLayout != _isStackLayout)
        {
            ApplyStackLayout(stackLayout);
        }

        if (_isStackLayout)
        {
            FiltersColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            CatalogSpacerColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            CatalogListColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            FiltersCard.Margin = _stackedFiltersMargin;
            CatalogListCard.Margin = _stackedListMargin;
            return;
        }

        FiltersColumnDefinition.Width = compactLayout
            ? new GridLength(260d, GridUnitType.Pixel)
            : _filtersColumnDefaultWidth;

        CatalogSpacerColumnDefinition.Width = _spacerColumnDefaultWidth;
        CatalogListColumnDefinition.Width = _listColumnDefaultWidth;
        FiltersCard.Margin = _filtersDefaultMargin;
        CatalogListCard.Margin = _listDefaultMargin;
    }

    private void ApplyStackLayout(bool stack)
    {
        if (stack)
        {
            Grid.SetRow(FiltersCard, 0);
            Grid.SetColumn(FiltersCard, 0);
            Grid.SetColumnSpan(FiltersCard, 3);
            Grid.SetRowSpan(FiltersCard, 1);

            Grid.SetRow(CatalogListCard, 1);
            Grid.SetColumn(CatalogListCard, 0);
            Grid.SetColumnSpan(CatalogListCard, 3);
            Grid.SetRowSpan(CatalogListCard, 1);
        }
        else
        {
            Grid.SetRow(FiltersCard, 0);
            Grid.SetColumn(FiltersCard, 0);
            Grid.SetColumnSpan(FiltersCard, 1);
            Grid.SetRowSpan(FiltersCard, 2);

            Grid.SetRow(CatalogListCard, 0);
            Grid.SetColumn(CatalogListCard, 2);
            Grid.SetColumnSpan(CatalogListCard, 1);
            Grid.SetRowSpan(CatalogListCard, 2);
        }

        _isStackLayout = stack;
    }
}
