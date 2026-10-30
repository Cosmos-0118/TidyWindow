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
    private enum Page { Overview, Install, Installed, Scout, Cleanup, Paths, Queue, Logs }
    private readonly ActivityLog log;
    private readonly WorkspaceViewModel model;
    private readonly LogsViewModel logs;
    private readonly InstallationsViewModel installations;
    private readonly OperationQueueViewModel operationQueue;
    private readonly ScoutViewModel scout;
    private readonly CleanupViewModel cleanup;
    private readonly PathDoctorViewModel paths;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer scoutStatusTimer;
    private string? scoutFolder;
    private long reconciledCompletions;
    private bool initialized;
    private bool closingDialog;
    private Page page;
    private readonly Windows.UI.ViewManagement.UISettings systemColors = new();
    private readonly AppearanceSettings appearanceSettings = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "appearance.json"));
    private Appearance appearance;
    public MainWindow(ActivityLog log)
    {
        this.log = log;
        var runner = new ProcessRunner(); var locator = new ExecutableLocator();
        var packages = new PackageService(runner, locator);
        var management = new InstallationService(packages, runner, locator);
        var executor = new PackageOperationExecutor(packages, management, locator, InstalledInventory.ReadWindows);
        operationQueue = new(PackageOperationQueue.WithLiveOutput(executor.ExecuteAsync), log, action => DispatcherQueue.TryEnqueue(() => action()));
        model = new(packages, log, operationQueue);
        installations = new(management, model, log, operationQueue);
        var scoutHost = Path.Combine(AppContext.BaseDirectory, "ScoutHost", "TidyWindow.SearchHost.exe");
        // Two long-running helpers: quick exact searches, and a separate one for slower near-match searches that can be stopped without disturbing typing.
        scout = new(new ScoutService(runner, scoutHost, worker: new PersistentScoutWorker(scoutHost), fuzzyWorker: new PersistentScoutWorker(scoutHost, killOnCancel: true)), log, action => DispatcherQueue.TryEnqueue(() => action()));
        var undo = new CleanupQuarantine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Undo"));
        cleanup = new(CleanupCatalog.Load(), new CleanupScanner(CleanupContext.Current), new CleanupCleaner(CleanupContext.Current, undo), undo, SystemCleanLauncher.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe")), log, action => DispatcherQueue.TryEnqueue(() => action()));
        _ = Task.Run(undo.Purge);
        var pathAnalyzer = new PathAnalyzer(Environment.ExpandEnvironmentVariables, Directory.Exists);
        var pathDoctor = new PathDoctor(new RegistryPathStore(), new PathBackup(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "PathBackups")), pathAnalyzer, EnvironmentBroadcast.Notify);
        paths = new(pathDoctor, new CommandResolver(File.Exists, IsStoreAlias, CommandResolver.DefaultExtensions()), new PathSetup(Environment.ExpandEnvironmentVariables, Directory.Exists, File.Exists, SafeSubdirectories), log, action => DispatcherQueue.TryEnqueue(() => action()), PickFolderAsync, OpenAliasSettings);
        InitializeComponent();
        Root.DataContext = model;
        CleanupPage.DataContext = cleanup;
        PathsPage.DataContext = paths;
        logs = new(log, action => DispatcherQueue.TryEnqueue(() => action()));
        LogsPage.DataContext = logs;
        InstalledPage.DataContext = installations;
        QueueButton.DataContext = operationQueue;
        QueuePage.DataContext = operationQueue;
        ScoutPage.DataContext = scout;
        scoutStatusTimer = DispatcherQueue.CreateTimer();
        scoutStatusTimer.Interval = TimeSpan.FromSeconds(3);
        scoutStatusTimer.Tick += async (_, _) => { if (page == Page.Scout && scout.NeedsBackendCheck) await RefreshScoutBackendAsync(); };
        operationQueue.StateChanged += QueueStateChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        ApplyBranding();
        PreparePages();
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
            if (page == Page.Installed) await installations.RefreshAsync();
        };
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { initialized = false; scoutStatusTimer.Stop(); systemColors.ColorValuesChanged -= SystemColorsChanged; operationQueue.StateChanged -= QueueStateChanged; operationQueue.Dispose(); scout.Dispose(); cleanup.Dispose(); paths.Dispose(); installations.Dispose(); logs.Dispose(); model.Dispose(); };
    }

    private void ApplyBranding()
    {
        // Missing art must never stop the app from opening.
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand");
        try { AppWindow.SetIcon(Path.Combine(folder, "tidywindow.ico")); } catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        var logo = Path.Combine(folder, "logo-64.png");
        if (File.Exists(logo)) TitleLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logo));
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
        // The sidebar names its items already; tooltips are only needed once the labels are hidden. They open beside the rail, not over the content.
        foreach (var (button, text) in new[] { (OverviewNav, "Overview"), (InstallNav, "Install apps"), (InstalledNav, "Installed apps"), (ScoutNav, "Scout"), (CleanupNav, "Cleanup"), (PathsNav, "Path Doctor"), (QueueButton, "Queue"), (LogsNav, "Logs"), (AppearanceNav, "Appearance") })
        {
            ToolTipService.SetPlacement(button, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Right);
            ToolTipService.SetToolTip(button, layout.NavigationRail ? text : null);
        }
        OverviewLabel.Visibility = InstallLabel.Visibility = InstalledLabel.Visibility = ScoutLabel.Visibility = CleanupLabel.Visibility = PathsLabel.Visibility = LogsLabel.Visibility = AppearanceLabel.Visibility = QueueLabel.Visibility = layout.NavigationRail ? Visibility.Collapsed : Visibility.Visible;
        ContentArea.Padding = new Thickness(layout.Padding, 24, layout.Padding, 24);
        model.ApplyLayout(layout);
        installations.ApplyLayout(layout);
        operationQueue.SetOutputHeight(Math.Clamp((Root.ActualHeight - 180) * 0.45, 96, 280));
        var wrapSearch = Root.ActualWidth < 640;
        var wrapScout = Root.ActualWidth < 900;
        Grid.SetColumnSpan(ScoutSearchBox, wrapScout ? 3 : 1);
        Grid.SetRow(ScoutLocation, wrapScout ? 1 : 0); Grid.SetColumn(ScoutLocation, wrapScout ? 0 : 1); Grid.SetColumnSpan(ScoutLocation, wrapScout ? 2 : 1);
        Grid.SetRow(ScoutType, wrapScout ? 1 : 0);
        ScoutLocationColumn.Width = wrapScout ? new GridLength(0) : new GridLength(180);
        Grid.SetColumnSpan(SearchBox, wrapSearch ? 3 : 1);
        Grid.SetRow(SourceFilter, wrapSearch ? 1 : 0);
        Grid.SetColumn(SourceFilter, wrapSearch ? 0 : 1);
        Grid.SetColumnSpan(SourceFilter, wrapSearch ? 2 : 1);
        Grid.SetRow(SearchButton, wrapSearch ? 1 : 0);
        FilterColumn.Width = new GridLength(wrapSearch ? 0 : 150);
        Grid.SetColumnSpan(InstalledSearch, wrapSearch ? 2 : 1);
        Grid.SetRow(InstalledFilter, wrapSearch ? 1 : 0);
        Grid.SetColumn(InstalledFilter, wrapSearch ? 0 : 1);
        Grid.SetColumnSpan(InstalledFilter, wrapSearch ? 2 : 1);
        InstalledFilterColumn.Width = wrapSearch ? new GridLength(0) : GridLength.Auto;
        var wrapSelection = Root.ActualWidth < 900;
        Grid.SetRow(SelectionActions, wrapSelection ? 1 : 0);
        Grid.SetColumn(SelectionActions, wrapSelection ? 0 : 1);
        Grid.SetColumnSpan(SelectionActions, wrapSelection ? 2 : 1);
        Grid.SetColumnSpan(LogSearch, wrapSearch ? 2 : 1);
        Grid.SetRow(LogSelects, wrapSearch ? 1 : 0);
        Grid.SetColumn(LogSelects, wrapSearch ? 0 : 1);
        Grid.SetColumnSpan(LogSelects, wrapSearch ? 2 : 1);
        LogSelectColumn.Width = new GridLength(wrapSearch ? 0 : 320);
        CopyLogsLabel.Visibility = OpenLogsLabel.Visibility = ClearLogsLabel.Visibility = Root.ActualWidth < 760 ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement PageElement(Page value) => value switch
    {
        Page.Install => InstallPage, Page.Installed => InstalledPage, Page.Scout => ScoutPage, Page.Cleanup => CleanupPage, Page.Paths => PathsPage, Page.Queue => QueuePage, Page.Logs => LogsPage, _ => OverviewPage
    };

    private bool pageShown;
    private void ShowPage(Page next)
    {
        if (pageShown && next == page) { if (next == Page.Install) SearchBox.Focus(FocusState.Programmatic); return; }
        page = next; pageShown = true;
        if (next == Page.Scout) scoutStatusTimer.Start(); else scoutStatusTimer.Stop();
        // Cheap work first, so the sidebar answers the click before anything else happens.
        UpdateNavSelection();
        foreach (var other in Enum.GetValues<Page>()) if (other != next) PageElement(other).Visibility = Visibility.Collapsed;
        Reveal(PageElement(next));
        if (page == Page.Install) SearchBox.Focus(FocusState.Programmatic);
    }

    // The incoming page is there at once and eases in over a split second, instead of snapping in.
    private void Reveal(UIElement element)
    {
        if (!systemColors.AnimationsEnabled) { element.Opacity = 1; element.Translation = default; element.Visibility = Visibility.Visible; return; }
        element.Opacity = 0; element.Translation = new System.Numerics.Vector3(0, 8, 0);
        element.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { element.Opacity = 1; element.Translation = default; });
    }

    private void PreparePages()
    {
        foreach (var value in Enum.GetValues<Page>())
        {
            var element = PageElement(value);
            element.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
            element.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        }
    }

    private void SystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (appearance == Appearance.System) ApplyAppearance();
        OutputPalette.Refresh();
    });
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
    private ElementTheme chromeTheme = ElementTheme.Default;
    private Brush navSelected = BrushCache.Transparent, navPrimary = BrushCache.Transparent, navMuted = BrushCache.Transparent, navAccent = BrushCache.Transparent;
    private void UpdateNavSelection()
    {
        foreach (var (button, target) in new[] { (OverviewNav, Page.Overview), (InstallNav, Page.Install), (InstalledNav, Page.Installed), (ScoutNav, Page.Scout), (CleanupNav, Page.Cleanup), (PathsNav, Page.Paths), (QueueButton, Page.Queue), (LogsNav, Page.Logs) })
        {
            var active = page == target;
            button.Background = active ? navSelected : BrushCache.Transparent;
            button.BorderBrush = active ? navAccent : BrushCache.Transparent;
            button.Foreground = active ? navPrimary : navMuted;
        }
    }
    // Theme-dependent work touches every log and queue row and the window frame, so it runs only when the theme really changes, never on a page switch.
    private void UpdateThemeChrome()
    {
        var theme = Root.ActualTheme;
        if (theme == chromeTheme) return;
        chromeTheme = theme;
        var light = theme == ElementTheme.Light;
        navSelected = BrushCache.Rgb(light ? 0xEBEBEB : 0x1F1F1F);
        navPrimary = BrushCache.Rgb(light ? 0x171717 : 0xFFFFFF);
        navMuted = BrushCache.Rgb(light ? 0x626262 : 0xA9A9A9);
        navAccent = new SolidColorBrush(systemColors.GetColorValue(light ? Windows.UI.ViewManagement.UIColorType.Accent : Windows.UI.ViewManagement.UIColorType.AccentLight2));
        UpdateNavSelection();
        AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, (byte)(light ? 0x17 : 0xFF), (byte)(light ? 0x17 : 0xFF), (byte)(light ? 0x17 : 0xFF));
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, (byte)(light ? 0x62 : 0xA9), (byte)(light ? 0x62 : 0xA9), (byte)(light ? 0x62 : 0xA9));
        foreach (var manager in model.Managers) manager.SetLightTheme(light);
        logs.SetLightTheme(light);
        operationQueue.SetLightTheme(light);
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
    private void CleanupClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Cleanup);
        // The first visit scans straight away; it takes a fraction of a second and fills in as results arrive.
        if (!cleanup.Scanned && !cleanup.Busy) _ = cleanup.ScanAsync();
    }
    private void PathsClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Paths);
        if (!paths.Loaded && !paths.Busy) _ = paths.RefreshAsync();
    }

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception ex)
        {
            log.Write(ActivityLevel.Warning, ActivityArea.App, "The folder picker could not open", ex.Message);
            return null;
        }
    }

    private static IEnumerable<string> SafeSubdirectories(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsStoreAlias(string file)
    {
        // Store shortcuts are empty link files in the WindowsApps folder; whether one really is a placeholder is decided by what it says when asked.
        try { return file.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (IOException) { return false; }
    }

    private void OpenAliasSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsfeatures-app-execution-aliases") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { log.Write(ActivityLevel.Warning, ActivityArea.App, "Windows settings could not open", ex.Message); }
    }

    private async void ScoutClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Scout);
        await RefreshScoutBackendAsync();
        if (initialized && page == Page.Scout) ScoutSearchBox.Focus(FocusState.Programmatic);
    }
    private async Task RefreshScoutBackendAsync()
    {
        var wasReady = scout.Indexed;
        await scout.CheckBackendAsync();
        if (initialized && !wasReady && scout.Indexed && page == Page.Scout) await SearchScoutAsync(false);
    }
    private string? ScoutRoot => ScoutLocation.SelectedIndex switch { 1 => null, >= 2 => scoutFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _ => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    private ScoutItemKind ScoutKind => (ScoutItemKind)Math.Clamp(ScoutType.SelectedIndex, 0, 2);
    private Task SearchScoutAsync(bool debounce = true) => scout.SearchAsync(ScoutSearchBox.Text, ScoutRoot, ScoutKind, debounce, ScoutLocation.SelectedIndex >= 2);
    private async void ScoutTextChanged(object sender, TextChangedEventArgs e) { if (initialized) await SearchScoutAsync(); }
    private async void ScoutTypeChanged(object sender, SelectionChangedEventArgs e) { if (initialized) await SearchScoutAsync(false); }
    private async void ScoutLocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        if (ScoutLocation.SelectedIndex == 2)
        {
            try
            {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) { ScoutLocation.SelectedIndex = scoutFolder is null ? 0 : 3; return; }
            scoutFolder = folder.Path; ScoutFolderChoice.Content = folder.DisplayName; ScoutFolderChoice.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(ScoutFolderChoice, scoutFolder);
            ScoutLocation.SelectedIndex = 3;
            }
            catch (Exception ex)
            {
                ScoutLocation.SelectedIndex = scoutFolder is null ? 0 : 3;
                scout.ReportError("The folder picker could not open. " + ex.Message);
                log.Write(ActivityLevel.Warning, ActivityArea.App, "Scout folder selection failed", ex.Message);
            }
            return;
        }
        await SearchScoutAsync(false);
    }
    private async void ScoutRefreshClick(object sender, RoutedEventArgs e)
    { scout.Cancel(); scout.RefreshFolder(); await scout.CheckBackendAsync(); await SearchScoutAsync(false); }
    private void ScoutCancelClick(object sender, RoutedEventArgs e) => scout.Cancel();
    private async void ScoutSetupClick(object sender, RoutedEventArgs e)
    {
        if (!scout.CanSetup) return;
        await RefreshScoutBackendAsync();
        if (!initialized || !scout.NeedsInstallation || !scout.CanSetup) return;
        if (!model.CanQueue) return;
        var manager = model.Managers.FirstOrDefault(x => x.Status?.Health == ManagerHealth.Ready && x.Definition.Kind is ManagerKind.Winget or ManagerKind.Chocolatey);
        if (manager is null) { ShowPage(Page.Overview); return; }
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Enable instant search?", Content = "This installs Everything, a local filename index, using your package manager. Its Windows service enables fast NTFS indexing. Windows may ask for administrator permission. Folder search remains available without it.", PrimaryButtonText = "Queue installation", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var kind = manager.Definition.Kind;
        model.QueueInstall(new(new(kind, kind == ManagerKind.Winget ? "voidtools.Everything" : "everything", "Everything", "", kind == ManagerKind.Winget ? "winget" : "chocolatey")));
        ShowPage(Page.Queue);
    }
    private async void ScoutOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ScoutRow row }) return;
        try
        {
            var folder = row.Entry.IsFolder ? row.Path : Path.GetDirectoryName(row.Path);
            if (folder is null || !Directory.Exists(folder)) throw new DirectoryNotFoundException("This location is no longer available. Refresh the search.");
            if (!await Launcher.LaunchFolderPathAsync(folder)) throw new IOException("File Explorer could not open this location.");
        }
        catch (Exception ex) { scout.ReportError(ex.Message); log.Write(ActivityLevel.Warning, ActivityArea.App, "Scout could not open this location", ex.Message); }
    }
    private void ScoutCopyClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: ScoutRow row }) TryCopy(row.Path); }
    private async void InstalledClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Installed);
        if (installations.NeedsRefresh) await installations.RefreshAsync();
    }
    private async void InstalledRefreshClick(object sender, RoutedEventArgs e) => await installations.RefreshAsync();
    private void InstalledFilterChanged(object sender, RoutedEventArgs e)
    { if (initialized) installations.Filter(InstalledSearch.Text, InstalledFilter.SelectedIndex == 1); }
    private void UpdateInstalledClick(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: InstalledRow row }) installations.QueueAction(row, PackageAction.Update); }
    private void SelectAllInstalledClick(object sender, RoutedEventArgs e) => installations.SelectAll(installations.AllSelected != true);
    private void UpdateSelectedClick(object sender, RoutedEventArgs e) => installations.QueueSelected(PackageAction.Update);
    private async void UninstallSelectedClick(object sender, RoutedEventArgs e)
    {
        var rows = installations.Selected.ToArray();
        if (rows.Length == 0) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Uninstall {rows.Length} apps?",
            Content = new ScrollViewer { MaxHeight = 300, Content = new TextBlock { Text = "Save your work and close these apps. Each will be uninstalled in order.\n\n" + string.Join("\n", rows.Select(x => $"{x.Name} · {x.Source}")), TextWrapping = TextWrapping.Wrap } },
            PrimaryButtonText = "Queue uninstalls", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            operationQueue.Enqueue(rows.Where(x => x.CanAct).Select(x => PackageOperation.Manage(x.Package, PackageAction.Uninstall)));
    }
    private void RemoveQueuedClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: QueueRow row }) operationQueue.Cancel(row); }
    private void ClearWaitingClick(object sender, RoutedEventArgs e) => operationQueue.CancelWaiting();
    private void ClearFinishedQueueClick(object sender, RoutedEventArgs e) => operationQueue.ClearFinished();
    private void QueueClick(object sender, RoutedEventArgs e) => ShowPage(Page.Queue);
    private void QueueRowClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: QueueRow row }) row.Toggle(); }
    private sealed class OutputScrollState { public double Offset; public bool Follow = true; }
    private void QueueOutputViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || e.IsIntermediate) return;
        var state = scroll.Tag as OutputScrollState ?? new();
        // Content growing is not a request to stop following. Only a changed scroll position is.
        if (Math.Abs(scroll.VerticalOffset - state.Offset) > 0.5) state.Follow = scroll.ScrollableHeight - scroll.VerticalOffset < 24;
        state.Offset = scroll.VerticalOffset; scroll.Tag = state;
    }
    private void QueueOutputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DependencyObject? parent = sender as DependencyObject;
        while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not ScrollViewer scroll || scroll.Tag is OutputScrollState { Follow: false }) return;
        DispatcherQueue.TryEnqueue(() => { if (scroll.Tag is not OutputScrollState { Follow: false }) scroll.ChangeView(null, scroll.ScrollableHeight, null, true); });
    }
    private async void QueueStateChanged()
    {
        scout.SetInstallationPending(operationQueue.Snapshot.Entries.Any(x => x.State is QueueItemState.Waiting or QueueItemState.Running
            && x.Operation.Kind == PackageOperationKind.Install
            && (x.Operation.Id.Equals("voidtools.Everything", StringComparison.OrdinalIgnoreCase) || x.Operation.Manager == ManagerKind.Chocolatey && x.Operation.Id.Equals("everything", StringComparison.OrdinalIgnoreCase))));
        if (operationQueue.HasWork || operationQueue.Snapshot.Completions <= reconciledCompletions) return;
        reconciledCompletions = operationQueue.Snapshot.Completions;
        try { await RefreshScoutBackendAsync(); await Refresh(); if (page == Page.Installed) await installations.RefreshAsync(); }
        catch (Exception ex) { log.Write(ActivityLevel.Warning, ActivityArea.Install, "Package status could not refresh after the queue finished", ex.Message); }
    }
    private async void UninstallInstalledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstalledRow row } || !row.CanAct) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Uninstall {row.Name}?",
            Content = new TextBlock { Text = $"This removes {row.Name} using {row.Package.ManagerName}{(row.Package.Global ? " for all users" : "")}. Save your work and close the app first.\n\nThe app’s uninstaller may ask for administrator permission. Personal settings are handled by the uninstaller; Scoop’s persistent data is kept.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) installations.QueueAction(row, PackageAction.Uninstall);
    }
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
        await model.RefreshAsync();
        installations.NeedsRefresh = true;
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
        if (sender is not FrameworkElement { DataContext: PackageRow row } || !row.CanInstall) return;
        var content = new StackPanel { Spacing = 14, MaxWidth = 480 };
        content.Children.Add(new TextBlock { Text = $"{row.Id}\n{row.Source} · available version {row.Version}", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = PackageService.DisplayInstallCommand(row.Package), FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = "This installs the latest version from this source and accepts the package’s license agreements. The package manager may open an installer or request administrator permission.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (row.Package.Manager == ManagerKind.Chocolatey)
            content.Children.Add(new TextBlock { Text = "Chocolatey will open in a separate administrator window. TidyWindow itself stays unelevated.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Install {row.Name}?", Content = content, PrimaryButtonText = "Install package", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) model.QueueInstall(row);
    }
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!model.Busy) return;
        args.Cancel = true;
        if (closingDialog) return;
        closingDialog = true;
        try
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Package work is still queued", Content = "Let the current operation finish before closing TidyWindow. You can remove waiting items using Queue in the sidebar.", CloseButtonText = "Keep open" }.ShowAsync();
        }
        finally { closingDialog = false; }
    }
}


