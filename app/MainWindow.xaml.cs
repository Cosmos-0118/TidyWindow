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
    private enum Page { Overview, Install, Logs }
    private readonly ActivityLog log;
    private readonly WorkspaceViewModel model;
    private readonly LogsViewModel logs;
    private bool initialized;
    private bool closingDialog;
    private Page page;
    private readonly Windows.UI.ViewManagement.UISettings systemColors = new();
    private readonly AppearanceSettings appearanceSettings = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "appearance.json"));
    private Appearance appearance;
    public MainWindow(ActivityLog log)
    {
        this.log = log;
        model = new(new PackageService(new ProcessRunner(), new ExecutableLocator()), log);
        InitializeComponent();
        Root.DataContext = model;
        logs = new(log, action => DispatcherQueue.TryEnqueue(() => action()));
        LogsPage.DataContext = logs;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new(1100, 740));
        appearance = appearanceSettings.Load();
        Root.ActualThemeChanged += (_, _) => UpdateThemeChrome();
        systemColors.ColorValuesChanged += SystemColorsChanged;
        ApplyAppearance();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 480;
            presenter.PreferredMinimumHeight = 420;
        }
        initialized = true;
        ShowPage(Page.Overview);
        Root.SizeChanged += (_, _) => ApplyResponsiveLayout();
        Root.Loaded += async (_, _) =>
        {
            FitInitialWindow();
            ApplyResponsiveLayout();
            await Refresh();
        };
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { systemColors.ColorValuesChanged -= SystemColorsChanged; logs.Dispose(); model.Dispose(); };
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
        OverviewLabel.Visibility = InstallLabel.Visibility = LogsLabel.Visibility = AppearanceLabel.Visibility = layout.NavigationRail ? Visibility.Collapsed : Visibility.Visible;
        ContentArea.Padding = new Thickness(layout.Padding, 24, layout.Padding, 24);
        model.ApplyLayout(layout);
        var wrapSearch = Root.ActualWidth < 640;
        Grid.SetColumnSpan(SearchBox, wrapSearch ? 3 : 1);
        Grid.SetRow(SourceFilter, wrapSearch ? 1 : 0);
        Grid.SetColumn(SourceFilter, wrapSearch ? 0 : 1);
        Grid.SetColumnSpan(SourceFilter, wrapSearch ? 2 : 1);
        Grid.SetRow(SearchButton, wrapSearch ? 1 : 0);
        FilterColumn.Width = new GridLength(wrapSearch ? 0 : 150);
        Grid.SetColumnSpan(LogSearch, wrapSearch ? 2 : 1);
        Grid.SetRow(LogSelects, wrapSearch ? 1 : 0);
        Grid.SetColumn(LogSelects, wrapSearch ? 0 : 1);
        Grid.SetColumnSpan(LogSelects, wrapSearch ? 2 : 1);
        LogSelectColumn.Width = new GridLength(wrapSearch ? 0 : 320);
        CopyLogsLabel.Visibility = OpenLogsLabel.Visibility = ClearLogsLabel.Visibility = Root.ActualWidth < 760 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowPage(Page next)
    {
        page = next;
        OverviewPage.Visibility = page == Page.Overview ? Visibility.Visible : Visibility.Collapsed;
        InstallPage.Visibility = page == Page.Install ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == Page.Logs ? Visibility.Visible : Visibility.Collapsed;
        UpdateThemeChrome();
        if (page == Page.Install) SearchBox.Focus(FocusState.Programmatic);
    }

    private void SystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (appearance == Appearance.System) ApplyAppearance(); });
    private void ApplyAppearance()
    {
        var background = systemColors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        var systemLight = background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 128;
        Root.RequestedTheme = appearance == Appearance.Light || appearance == Appearance.System && systemLight ? ElementTheme.Light : ElementTheme.Dark;
        SystemThemeItem.IsChecked = appearance == Appearance.System;
        LightThemeItem.IsChecked = appearance == Appearance.Light;
        DarkThemeItem.IsChecked = appearance == Appearance.Dark;
        UpdateThemeChrome();
    }
    private void UpdateThemeChrome()
    {
        var light = Root.ActualTheme == ElementTheme.Light;
        var selected = light ? ColorHelper.FromArgb(255, 235, 235, 235) : ColorHelper.FromArgb(255, 31, 31, 31);
        var primary = light ? ColorHelper.FromArgb(255, 23, 23, 23) : Colors.White;
        var muted = light ? ColorHelper.FromArgb(255, 98, 98, 98) : Colors.DarkGray;
        foreach (var (button, target) in new[] { (OverviewNav, Page.Overview), (InstallNav, Page.Install), (LogsNav, Page.Logs) })
        {
            button.Background = new SolidColorBrush(page == target ? selected : Colors.Transparent);
            button.Foreground = new SolidColorBrush(page == target ? primary : muted);
        }
        AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = primary;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = muted;
        foreach (var manager in model.Managers) manager.SetLightTheme(light);
        logs.SetLightTheme(light);
    }
    private void ThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || !Enum.TryParse<Appearance>(item.Tag?.ToString(), out var choice)) return;
        appearance = choice;
        ApplyAppearance();
        try { appearanceSettings.Save(choice); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToolTipService.SetToolTip(item, "Appearance changed, but the preference could not be saved: " + ex.Message);
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Appearance preference could not be saved", ex.Message);
        }
    }
    private void OverviewClick(object sender, RoutedEventArgs e) => ShowPage(Page.Overview);
    private void InstallClick(object sender, RoutedEventArgs e) => ShowPage(Page.Install);
    private void LogsClick(object sender, RoutedEventArgs e) => ShowPage(Page.Logs);
    private void SearchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { ShowPage(Page.Install); args.Handled = true; }

    private void ApplyLogFilter()
    {
        if (!initialized) return;
        ActivityArea? area = LogAreaFilter.SelectedIndex switch { 1 => ActivityArea.Managers, 2 => ActivityArea.Search, 3 => ActivityArea.Install, 4 => ActivityArea.App, _ => null };
        var level = LogLevelFilter.SelectedIndex switch { 1 => ActivityLevel.Warning, 2 => ActivityLevel.Error, _ => ActivityLevel.Info };
        logs.SetFilter(new ActivityFilter(level, area, LogSearch.Text));
    }
    private void LogFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyLogFilter();
    private void LogFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyLogFilter();
    private void LogRowClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: LogRow row }) row.Toggle(); }
    private static bool TryCopy(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return false; }
    }
    private async void CopyLogsClick(object sender, RoutedEventArgs e)
    {
        if (logs.Rows.Count == 0) return;
        var ok = TryCopy(ActivityLog.Format(logs.Visible));
        CopyLogsLabel.Text = ok ? "Copied" : "Clipboard busy";
        await Task.Delay(1500);
        CopyLogsLabel.Text = "Copy";
    }
    private async void CopyEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LogRow row } || !TryCopy(ActivityLog.Format([row.Entry]))) return;
        row.SetCopied(true);
        await Task.Delay(1500);
        row.SetCopied(false);
    }
    private async void OpenLogsFolderClick(object sender, RoutedEventArgs e)
    {
        if (log.Folder is null) return;
        try { Directory.CreateDirectory(log.Folder); await Launcher.LaunchFolderPathAsync(log.Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { log.Write(ActivityLevel.Warning, ActivityArea.App, "The log folder could not be opened", ex.Message); }
    }
    private async void ClearLogsClick(object sender, RoutedEventArgs e)
    {
        if (log.Count == 0) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Clear all logs?", Content = "This deletes every saved entry from this computer. It does not undo anything TidyWindow has done.", PrimaryButtonText = "Clear logs", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) log.Clear();
    }
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
        if (SearchBox.Text.Trim().Length >= 2 && !model.Searching && model.Packages.Count == 0)
            await model.SearchAsync(SearchBox.Text, Filter, false);
    }
    private async void ManagerRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ManagerRow row })
        {
            if (row.Status?.Health == ManagerHealth.Restricted)
                await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "This manager is restricted", Content = new TextBlock { Text = row.Detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "Close" }.ShowAsync();
            else await model.RecoverAsync(row);
        }
    }
    private async void InstallPackageClick(object sender, RoutedEventArgs e)
    {
        if (model.Busy || sender is not FrameworkElement { DataContext: PackageRow row } || !row.CanInstall) return;
        var content = new StackPanel { Spacing = 14, MaxWidth = 480 };
        content.Children.Add(new TextBlock { Text = $"{row.Id}\n{row.Source} · available version {row.Version}", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = PackageService.DisplayInstallCommand(row.Package), FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = "This installs the latest version from this source and accepts the package’s license agreements. The package manager may open an installer or request administrator permission.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (row.Package.Manager == ManagerKind.Chocolatey)
            content.Children.Add(new TextBlock { Text = "Chocolatey will open in a separate administrator window. TidyWindow itself stays unelevated.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Install {row.Name}?", Content = content, PrimaryButtonText = "Install package", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await model.InstallAsync(row);
    }
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!model.Busy) return;
        args.Cancel = true;
        if (closingDialog) return;
        closingDialog = true;
        try
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "An operation is still running", Content = "Please let the package manager finish before closing TidyWindow. Follow any Windows permission prompt or installer window that opens.", CloseButtonText = "Keep open" }.ShowAsync();
        }
        finally { closingDialog = false; }
    }
}


