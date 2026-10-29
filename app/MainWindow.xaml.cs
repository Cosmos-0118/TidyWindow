using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;
using Windows.System;

namespace TidyWindow.Next.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly WorkspaceViewModel model = new(new PackageService(new ProcessRunner(), new ExecutableLocator()));
    private bool initialized;
    private bool closingDialog;
    public MainWindow()
    {
        InitializeComponent();
        Root.DataContext = model;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new(1100, 740));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Gray;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 480;
            presenter.PreferredMinimumHeight = 420;
        }
        initialized = true;
        ShowPage(false);
        Root.SizeChanged += (_, _) => ApplyResponsiveLayout();
        Root.Loaded += async (_, _) =>
        {
            FitInitialWindow();
            ApplyResponsiveLayout();
            await Refresh();
        };
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => model.Dispose();
    }

    private void FitInitialWindow()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = Root.XamlRoot.RasterizationScale;
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        presenter.PreferredMinimumWidth = Math.Min(area.Width, (int)(480 * scale));
        presenter.PreferredMinimumHeight = Math.Min(area.Height, (int)(420 * scale));
        if (presenter.State == OverlappedPresenterState.Maximized) return;
        var width = Math.Min((int)(1100 * scale), Math.Max(1, area.Width - (int)(32 * scale)));
        var height = Math.Min((int)(740 * scale), Math.Max(1, area.Height - (int)(32 * scale)));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private void ApplyResponsiveLayout()
    {
        var layout = AdaptiveLayout.ForWidth(Root.ActualWidth);
        SidebarColumn.Width = new GridLength(layout.SidebarWidth);
        OverviewLabel.Visibility = InstallLabel.Visibility = layout.NavigationRail ? Visibility.Collapsed : Visibility.Visible;
        ContentArea.Padding = new Thickness(layout.Padding, 24, layout.Padding, 24);
        model.ApplyLayout(layout);
        var wrapSearch = Root.ActualWidth < 640;
        Grid.SetColumnSpan(SearchBox, wrapSearch ? 3 : 1);
        Grid.SetRow(SourceFilter, wrapSearch ? 1 : 0);
        Grid.SetColumn(SourceFilter, wrapSearch ? 0 : 1);
        Grid.SetColumnSpan(SourceFilter, wrapSearch ? 2 : 1);
        Grid.SetRow(SearchButton, wrapSearch ? 1 : 0);
        FilterColumn.Width = new GridLength(wrapSearch ? 0 : 150);
    }

    private void ShowPage(bool install)
    {
        OverviewPage.Visibility = install ? Visibility.Collapsed : Visibility.Visible;
        InstallPage.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
        OverviewNav.Background = new SolidColorBrush(install ? Colors.Transparent : ColorHelper.FromArgb(255, 31, 31, 31));
        InstallNav.Background = new SolidColorBrush(install ? ColorHelper.FromArgb(255, 31, 31, 31) : Colors.Transparent);
        OverviewNav.Foreground = new SolidColorBrush(install ? Colors.DarkGray : Colors.White);
        InstallNav.Foreground = new SolidColorBrush(install ? Colors.White : Colors.DarkGray);
        if (install) SearchBox.Focus(FocusState.Programmatic);
    }
    private void OverviewClick(object sender, RoutedEventArgs e) => ShowPage(false);
    private void InstallClick(object sender, RoutedEventArgs e) => ShowPage(true);
    private void SearchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { ShowPage(true); args.Handled = true; }
    private ManagerKind? Filter => SourceFilter.SelectedIndex switch { 1 => ManagerKind.Winget, 2 => ManagerKind.Chocolatey, 3 => ManagerKind.Scoop, _ => null };
    private async void SearchTextChanged(object sender, TextChangedEventArgs e) { if (initialized) await model.SearchAsync(SearchBox.Text, Filter); }
    private async void SourceChanged(object sender, SelectionChangedEventArgs e) { if (initialized) await model.SearchAsync(SearchBox.Text, Filter, false); }
    private async void SearchClick(object sender, RoutedEventArgs e) => await model.SearchAsync(SearchBox.Text, Filter, false);
    private async void SearchKeyDown(object sender, KeyRoutedEventArgs e)
    { if (e.Key == VirtualKey.Enter) { e.Handled = true; await model.SearchAsync(SearchBox.Text, Filter, false); } }
    private void CancelSearchClick(object sender, RoutedEventArgs e) => model.CancelSearch();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh();
    private async Task Refresh()
    {
        DetectionProgress.Visibility = Visibility.Visible;
        try { await model.RefreshAsync(); }
        finally { DetectionProgress.Visibility = Visibility.Collapsed; }
        if (SearchBox.Text.Trim().Length >= 2) await model.SearchAsync(SearchBox.Text, Filter, false);
    }
    private async void ManagerGuideClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ManagerRow row })
            await Launcher.LaunchUriAsync(new Uri(row.Definition.SetupUrl));
    }
    private async void InstallPackageClick(object sender, RoutedEventArgs e)
    {
        if (model.Installing || sender is not FrameworkElement { DataContext: PackageRow row } || !row.CanInstall) return;
        var content = new StackPanel { Spacing = 14, MaxWidth = 480 };
        content.Children.Add(new TextBlock { Text = $"{row.Id}\n{row.Source} · available version {row.Version}", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = PackageService.DisplayInstallCommand(row.Package), FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = "This installs the latest version from this source and accepts the package’s license agreements. The package manager may open an installer or request administrator permission.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (row.Package.Manager == ManagerKind.Chocolatey)
            content.Children.Add(new TextBlock { Text = "Chocolatey will open in a separate administrator window. TidyWindow itself stays unelevated.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = $"Install {row.Name}?", Content = content, PrimaryButtonText = "Install package", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await model.InstallAsync(row);
    }
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!model.Installing) return;
        args.Cancel = true;
        if (closingDialog) return;
        closingDialog = true;
        try
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "An installation is still running", Content = "Please let the package manager finish before closing TidyWindow. Check its installer window if it needs your attention.", CloseButtonText = "Keep open" }.ShowAsync();
        }
        finally { closingDialog = false; }
    }
}
