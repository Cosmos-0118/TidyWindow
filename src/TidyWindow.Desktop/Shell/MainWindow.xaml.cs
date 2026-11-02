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
    private enum Page { Overview, Install, Installed, Scout, Cleanup, Paths, Repair, Startup, Quiet, Tune, Debloat, Fixes, Unstick, Lab, Queue, Logs, Settings }
    private readonly ActivityLog log;
    private readonly WorkspaceViewModel model;
    private readonly LogsViewModel logs;
    private readonly InstallationsViewModel installations;
    private readonly OperationQueueViewModel operationQueue;
    private readonly ScoutViewModel scout;
    private readonly CleanupViewModel cleanup;
    private readonly PathDoctorViewModel paths;
    private readonly RepairViewModel repair;
    private readonly ToastCenter toasts;
    private readonly RepairSession repairSession;
    private readonly StartupViewModel startup;
    private readonly StartupSession startupSession;
    private readonly BackgroundViewModel quiet;
    private readonly BackgroundSession quietSession;
    private readonly TuneViewModel tune;
    private readonly TuneSession tuneSession;
    private readonly DebloatViewModel debloat;
    private readonly DebloatSession debloatSession;
    private readonly FixesViewModel fixes;
    private readonly FixSession fixSession;
    private readonly UnstickViewModel unstick;
    private readonly LabViewModel lab;
    private readonly SettingsViewModel settings;
    private readonly PathFinder pathFinder;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer scoutStatusTimer;
    private string? scoutFolder;
    private long reconciledCompletions;
    private bool initialized;
    private bool closingDialog;
    private volatile bool closeConfirmed;
    private ContentDialog? openDialog;
    private Task<ContentDialogResult>? openDialogShowing;
    private readonly InterruptedWork interrupted;
    private InterruptedOperation? previousRun;
    private Page page;
    private readonly AppearanceSettings appearanceSettings = new(AppearanceSettings.DefaultPath);
    private Appearance appearance;
    public MainWindow(ActivityLog log)
    {
        this.log = log;
        Open = this;
        toasts = new(Post, log);
        var runner = new ProcessRunner(); var locator = new ExecutableLocator();
        var packages = new PackageService(runner, locator, new ChocolateyFeed(), new ScoopCatalog());
        // Repairs to Windows-wide records go through a helper that only runs after the person approves the Windows permission prompt.
        // An uninstall that fails uses the same repair engine, so the helper is shared.
        var installSystem = new WindowsInstallSystem();
        var repairHistory = new RepairHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "RepairBackups"));
        repairSession = RepairSession.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe"));
        var repairService = new RepairService(installSystem, RegistryRepair.User, repairSession, repairSession.OpenAsync, repairSession.Close, repairHistory, new WindowsInstallerOps(), repairSession, new WingetFetcher(), FileShortcutEditor.User(), repairSession, FileFolderEditor.User(), repairSession);
        var uninstallRecovery = new UninstallRecovery(installSystem, new WindowsUninstallHost(), runner, repairService) { AllowDownload = true };
        var management = new InstallationService(packages, runner, locator, new SkippedUpdates(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "skipped-updates.json")), uninstallRecovery);
        var executor = new PackageOperationExecutor(packages, management, locator, InstalledInventory.ReadWindows);
        // Fixes share the queue, and so its live output. Machine-wide fixes run in a helper that sends its output back as it is produced.
        fixSession = FixSession.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe"));
        var fixStore = new FixRunStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Fixes"));
        var fixService = new FixService(FixCatalog.Load(), new FixStepExecutor(), fixSession, FixService.IsAdministrator, () => fixSession.OpenAsync(), fixStore, helperOpen: () => fixSession.IsOpen);
        var packageQueue = PackageOperationQueue.WithLiveOutput((operation, progress, token) => operation.Fix is { } run ? fixService.RunAsync(run, progress, token) : executor.ExecuteAsync(operation, progress, token));
        // A note is kept while an operation runs, so a run that ended partway (closed, killed, crashed) is reported at the next launch.
        interrupted = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "interrupted-operation.json"));
        previousRun = interrupted.Take();
        packageQueue.Started += entry => interrupted.Begin(entry.Operation);
        // Closing from the warning stops the work on purpose but keeps the note, because the work was still cut short.
        packageQueue.Finished += _ => { if (!closeConfirmed) interrupted.End(); };
        operationQueue = new(packageQueue, log, Post);
        fixes = new(FixCatalog.Load(), operationQueue, fixStore, toasts, () => ShowPage(Page.Queue));
        model = new(packages, log, operationQueue);
        installations = new(management, model, log, operationQueue);
        var scoutHost = Path.Combine(AppContext.BaseDirectory, "ScoutHost", "TidyWindow.SearchHost.exe");
        // Two long-running helpers: quick exact searches, and a separate one for slower near-match searches that can be stopped without disturbing typing.
        scout = new(new ScoutService(runner, scoutHost, worker: new PersistentScoutWorker(scoutHost), fuzzyWorker: new PersistentScoutWorker(scoutHost, killOnCancel: true)), log, Post);
        var undo = new CleanupQuarantine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Undo"));
        cleanup = new(CleanupCatalog.Load(), new CleanupScanner(CleanupContext.Current), new CleanupCleaner(CleanupContext.Current, undo), undo, SystemCleanLauncher.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe")), log, Post, toasts);
        _ = Task.Run(undo.Purge);
        var pathAnalyzer = new PathAnalyzer(Environment.ExpandEnvironmentVariables, Directory.Exists);
        var pathBackups = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "PathBackups");
        var registry = new RegistryPathStore();
        var pathDoctor = new PathDoctor(registry, new PathBackup(pathBackups), pathAnalyzer, EnvironmentBroadcast.Notify);
        // Windows-wide edits go through a helper that only runs after the person approves the Windows permission prompt.
        var machineSession = MachinePathSession.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe"));
        var machineDoctor = new PathDoctor(new ElevatedPathStore(registry, machineSession), new PathBackup(Path.Combine(pathBackups, "machine")), pathAnalyzer, EnvironmentBroadcast.Notify, PathScope.Machine);
        pathFinder = new PathFinder(Environment.ExpandEnvironmentVariables, SafeFolders, SafeFiles);
        paths = new(pathDoctor, machineDoctor, machineSession, new CommandResolver(File.Exists, IsStoreAlias, CommandResolver.DefaultExtensions()), new PathSetup(Environment.ExpandEnvironmentVariables, Directory.Exists, File.Exists, SafeSubdirectories), log, Post, toasts, ChooseFolderAsync, OpenAliasSettings);
        InitializeComponent();
        ToastStack.ItemsSource = toasts.Items;
        Root.DataContext = model;
        repair = new(installSystem, repairService, repairHistory, log, toasts, chooseFolder: PickFolderAsync);
        // Turning off a machine-wide item (a service, or something for every user) goes through a helper that only runs after the permission prompt.
        var startupSystem = new WindowsStartupSystem();
        startupSession = StartupSession.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe"));
        var startupHistory = new StartupHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Startup"));
        startup = new(startupSystem, new StartupService(new LocalStartupChanger(startupSystem, new WindowsStartupWriter()), startupSession, () => startupSession.OpenAsync(), startupHistory), log, toasts);
        // The same goes for the services, scheduled tasks and Windows settings behind Quiet Windows.
        var quietSystem = new WindowsBackgroundSystem();
        var quietCatalog = BackgroundCatalog.Load();
        quietSession = BackgroundSession.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe"));
        var quietHistory = new BackgroundHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Background"));
        quiet = new(quietSystem, quietCatalog, new BackgroundManager(new LocalBackgroundChanger(quietSystem, new WindowsBackgroundWriter(), quietCatalog), quietSession, () => quietSession.OpenAsync(), quietHistory), log, toasts);
        // Tune changes your own settings directly; settings for every user go through the same kind of helper, after the permission prompt.
        var tuneSystem = new WindowsTuneSystem();
        var tuneCatalog = TuneCatalog.Load();
        tuneSession = TuneSession.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe"));
        var tuneHistory = new TuneHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Tune"));
        // Removing apps needs administrator rights, so the page never removes anything itself: it asks the helper, after the permission prompt.
        // The page reads the PC without them. The helper reads it again for every account and checks the same rules.
        var debloatCatalog = DebloatCatalog.Load();
        var debloatSystem = new WindowsDebloatSystem(elevated: false);
        debloatSession = DebloatSession.Elevated(Path.Combine(AppContext.BaseDirectory, "CleanHost", "TidyWindow.CleanHost.exe"));
        var debloatHistory = new DebloatHistory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Debloat"));
        debloat = new(debloatSystem, debloatCatalog, new DebloatManager(debloatSession, () => debloatSession.OpenAsync(), debloatHistory, new DebloatReinstaller(runner, locator)), log, toasts);
        var labStore = new LabStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Lab"));
        lab = new(new PowerMode(new PowerCfgProcess(), labStore), labStore, new ProcessBoost(new LiveProcesses()), tuneSystem, log, toasts, OpenTune, PowerSource.HasBattery());
        tune = new(tuneSystem, tuneCatalog, new TuneManager(new LocalTuneChanger(tuneSystem, new WindowsTuneWriter(), tuneCatalog), tuneSession, () => tuneSession.OpenAsync(), tuneHistory), log, toasts, ExplorerShell.RestartAsync);
        unstick = new(new UnstickRunner(new WindowsUnstickSystem(), (run, progress, token) => fixService.RunAsync(run, progress, token)), log, toasts);
        CleanupPage.DataContext = cleanup;
        RepairPage.DataContext = repair;
        StartupPage.DataContext = startup;
        QuietPage.DataContext = quiet;
        TunePage.DataContext = tune;
        DebloatPage.DataContext = debloat;
        FixesPage.DataContext = fixes;
        UnstickPage.DataContext = unstick;
        LabPage.DataContext = lab;
        PathsPage.DataContext = paths;
        logs = new(log, Post);
        settings = new(log, toasts, () => operationQueue.HasWork, () => ShowPage(Page.Logs), Post);
        // Problems a page cannot show in its own empty space arrive as a toast, with the full text kept in Logs.
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.Errors) && model.Errors.Length > 0) ReportProblem("search-errors", "Search needs attention", model.Errors); };
        installations.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(installations.Errors) && installations.Errors.Length > 0) ReportProblem("installed-errors", "Some apps or updates could not be checked", installations.Errors); };
        scout.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(scout.Error) && scout.Error.Length > 0) ReportProblem("scout-error", "Search needs attention", scout.Error); };
        LogsPage.DataContext = logs;
        SettingsPage.DataContext = settings;
        InstalledPage.DataContext = installations;
        QueueButton.DataContext = operationQueue;
        QueuePage.DataContext = operationQueue;
        ScoutPage.DataContext = scout;
        scoutStatusTimer = DispatcherQueue.CreateTimer();
        scoutStatusTimer.Interval = TimeSpan.FromSeconds(3);
        scoutStatusTimer.Tick += (_, _) => { if (page == Page.Scout && scout.NeedsBackendCheck) Run(RefreshScoutBackendAsync, "checking the search index"); };
        operationQueue.StateChanged += QueueStateChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        ApplyBranding();
        PreparePages();
        AppWindow.Resize(new(1100, 740));
        appearance = appearanceSettings.Load();
        Root.ActualThemeChanged += (_, _) => UpdateThemeChrome();
        if (!SystemUi.Watch(SystemColorsChanged)) log.Write(ActivityLevel.Warning, ActivityArea.App, "Windows did not report its colour settings", "The theme and accent colour stay as they were when TidyWindow opened.");
        ApplyAppearance();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 480;
            presenter.PreferredMinimumHeight = 420;
        }
        // Placed before the window is first shown, so it opens where it will stay instead of moving once it is on screen.
        FitInitialWindow();
        initialized = true;
        PrepareSidebar();
        ShowPage(Page.Overview);
        Root.SizeChanged += (_, _) => ApplyResponsiveLayout();
        // The sidebar stays open while the theme menu it launched is showing.
        AppearanceMenu.Opened += (_, _) => { sidebarMenu = true; UpdateSidebar(); };
        AppearanceMenu.Closed += (_, _) => { sidebarMenu = false; UpdateSidebar(); };
        Root.Loaded += (_, _) => Run(async () =>
        {
            WatchFirstFrames();
            ReportInterruptedRun();
            ApplyResponsiveLayout();
            await Refresh();
            if (page == Page.Installed) await installations.RefreshAsync();
        }, "starting up");
        AppWindow.Closing += OnClosing;
        // A toast waits while the window is not in use, so it cannot come and go unseen; and the stack never grows taller than the window.
        Activated += (_, e) => toasts.Attentive = e.WindowActivationState != WindowActivationState.Deactivated;
        Root.SizeChanged += (_, e) => ToastScroll.MaxHeight = Math.Max(120, e.NewSize.Height - 32);
        Closed += (_, _) => { initialized = false; Open = null; scoutStatusTimer.Stop(); SystemUi.Unwatch(SystemColorsChanged); operationQueue.StateChanged -= QueueStateChanged; operationQueue.Dispose(); scout.Dispose(); cleanup.Dispose(); paths.Dispose(); repair.Dispose(); repairSession.Dispose(); startup.Dispose(); startupSession.Dispose(); quiet.Dispose(); quietSession.Dispose(); tune.Dispose(); tuneSession.Dispose(); debloat.Dispose(); debloatSession.Dispose(); fixes.Dispose(); fixSession.Dispose(); unstick.Dispose(); lab.Dispose(); settings.Dispose(); toasts.Dispose(); installations.Dispose(); logs.Dispose(); model.Dispose(); };
    }

    /// <summary>The window that is open, for a page that needs <see cref="ShowDialogAsync"/>, <see cref="Post"/> or <see cref="Run"/>; null once it has closed.</summary>
    internal static MainWindow? Open { get; private set; }

    /// <summary>
    /// Runs something on the UI thread, from any thread. Every view model reaches the window through this, so an exception in a posted
    /// action is recorded instead of ending the process (the dispatcher raises it where the app's error handlers may never see it), and an
    /// action the queue would not take, because the window is shutting down, is noted rather than lost without trace.
    /// </summary>
    public void Post(Action action)
    {
        var queued = DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { log.Write(ActivityLevel.Error, ActivityArea.App, "A page could not update", ex.ToString()); }
        });
        if (!queued) log.Write(ActivityLevel.Warning, ActivityArea.App, "An update arrived after the window had closed", action.Method.DeclaringType?.ToString());
    }

    /// <summary>
    /// The exception barrier for work started by a click, a timer or a page event. Whatever it throws is recorded in Logs and reported with a
    /// toast, instead of ending the process the way an exception escaping an event handler does. <paramref name="doing"/> says what the work
    /// was, in plain words that fit after "while": "refreshing the package managers". Cancelled work is not reported; leaving a page or
    /// closing the window cancels whatever was running.
    /// </summary>
    public async void Run(Func<Task> work, string doing)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Write(ActivityLevel.Error, ActivityArea.App, "Something went wrong while " + doing, ex.ToString());
            toasts.Show("failed:" + doing, ToastKind.Warning, "Something went wrong", $"TidyWindow could not finish {doing}. The details are in Logs.", "Open Logs", () => { ShowPage(Page.Logs); return Task.CompletedTask; }, 15);
        }
    }

    /// <summary>
    /// Shows a dialog over the window. WinUI allows one at a time, and a second ShowAsync throws (fatal from an event handler), so a dialog
    /// asked for while another is up is not shown and answers <see cref="ContentDialogResult.None"/>, as if it had been dismissed. The window's
    /// XamlRoot and theme are filled in when the dialog has none. Once the window has closed it answers None as well.
    /// </summary>
    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (!initialized || openDialog is not null) return ContentDialogResult.None;
        if (dialog.XamlRoot is null) dialog.XamlRoot = Root.XamlRoot;
        if (dialog.RequestedTheme == ElementTheme.Default) dialog.RequestedTheme = Root.ActualTheme;
        openDialog = dialog;
        try { return await (openDialogShowing = dialog.ShowAsync().AsTask()); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            log.Write(ActivityLevel.Warning, ActivityArea.App, "A dialog could not open", ex.ToString());
            return ContentDialogResult.None;
        }
        finally { if (ReferenceEquals(openDialog, dialog)) { openDialog = null; openDialogShowing = null; } }
    }

    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes once the window has laid out and drawn its first frames, which is when it is ready to be seen.</summary>
    internal Task FirstFrame => firstFrame.Task;

    /// <summary>Waits for a few frames to be drawn after the content loads, so the first one that is seen is not the half-built one.</summary>
    private void WatchFirstFrames()
    {
        var frames = 0;
        void Rendered(object? sender, object e)
        {
            if (++frames < 3) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= Rendered;
            firstFrame.TrySetResult();
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += Rendered;
    }

    private void ReportInterruptedRun()
    {
        if (previousRun is not { } last) return;
        previousRun = null;
        var title = $"Last session ended while {last.Kind} {last.Name}";
        var detail = $"{last.Source} was {last.Kind} {last.Name} when TidyWindow last stopped (started {last.Started:g}). It may be incomplete or damaged. Check it under Installed apps, and run it again if needed.";
        log.Write(ActivityLevel.Warning, ActivityArea.Install, title, detail);
        toasts.Show("interrupted-run", ToastKind.Warning, title, ToastCenter.Brief(detail), "Open Installed apps",
            async () => { ShowPage(Page.Installed); if (installations.NeedsRefresh) await installations.RefreshAsync(); }, 0);
    }

    private void ApplyBranding()
    {
        // Missing art must never stop the app from opening.
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Brand");
        try { AppWindow.SetIcon(Path.Combine(folder, "tidywindow.ico")); } catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        var logo = Path.Combine(folder, "logo-64.png");
        if (File.Exists(logo)) TitleLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logo)) { DecodePixelWidth = 18, DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical };
    }

    private void FitInitialWindow()
    {
        // The same rectangle the splash already covers, worked out once for both.
        var area = StartupFrame.WorkArea;
        var scale = StartupFrame.Scale;
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        presenter.PreferredMinimumWidth = Math.Min(area.Width, (int)(480 * scale));
        presenter.PreferredMinimumHeight = Math.Min(area.Height, (int)(420 * scale));
        if (presenter.State == OverlappedPresenterState.Maximized) return;
        AppWindow.MoveAndResize(StartupFrame.Rect);
    }

    private void ApplyResponsiveLayout()
    {
        var layout = AdaptiveLayout.ForWidth(Root.ActualWidth, sidebarReserved);
        SidebarColumn.Width = new GridLength(layout.SidebarWidth);
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

    // The sidebar opens a moment after the pointer arrives and closes a little after it leaves, so crossing the edge of the window does not make it flicker.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? sidebarTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? navThumbTimer;
    private bool navThumbHold;
    private bool sidebarOpen, sidebarReserved, sidebarPointer, sidebarKeyboard, sidebarMenu, keyboardUsed;
    private Microsoft.UI.Composition.CompositionPropertySet? sidebarState;
    private readonly bool sidebarAnimations = SystemUi.AnimationsEnabled;

    private void SidebarPointerEntered(object sender, PointerRoutedEventArgs e) { sidebarPointer = true; UpdateSidebar(); PlaceNavThumb(); }
    private void SidebarPointerExited(object sender, PointerRoutedEventArgs e) { sidebarPointer = false; UpdateSidebar(); PlaceNavThumb(); }
    // The first control to get focus when the app starts is in the sidebar; only focus the person moved there with the keyboard opens it.
    private void SidebarGotFocus(object sender, RoutedEventArgs e) { sidebarKeyboard = keyboardUsed && (e.OriginalSource as Control)?.FocusState == FocusState.Keyboard; UpdateSidebar(); }
    private void SidebarLostFocus(object sender, RoutedEventArgs e) { sidebarKeyboard = false; UpdateSidebar(); }

    // The rail is too narrow for a scrollbar. The wheel still scrolls. The hairline fades in while the pointer is here, and fades out a moment after it leaves.
    private Microsoft.UI.Composition.Visual? navThumbVisual;
    private bool navThumbShown;
    private double navThumbHeight = -1;

    private void NavScrolled(object sender, ScrollViewerViewChangedEventArgs e)
    {
        navThumbHold = true;
        PlaceNavThumb();
        navThumbTimer ??= DispatcherQueue.CreateTimer();
        navThumbTimer.Interval = TimeSpan.FromMilliseconds(600);
        navThumbTimer.IsRepeating = false;
        navThumbTimer.Tick -= NavThumbFaded;
        navThumbTimer.Tick += NavThumbFaded;
        navThumbTimer.Start();
    }

    private void NavThumbFaded(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) { navThumbHold = false; PlaceNavThumb(); }

    private void NavScrollSized(object sender, SizeChangedEventArgs e) => PlaceNavThumb();

    private void PlaceNavThumb()
    {
        if (navThumbVisual is null)
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(NavThumb, true);
            navThumbVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(NavThumb);
            NavThumb.Opacity = 1;
            navThumbVisual.Opacity = 0;
        }
        var extent = NavScroll.ExtentHeight;
        var viewport = NavScroll.ViewportHeight;
        var track = NavScroll.ActualHeight;
        // The thumb is at least 28 px, so a rail shorter than that shows none (and Clamp refuses a range that ends below where it starts).
        var canScroll = extent > viewport + 0.5 && track >= 28;
        if (canScroll)
        {
            var thumb = Math.Clamp(viewport / extent * track, 28, track);
            if (Math.Abs(thumb - navThumbHeight) > 0.5)
            {
                navThumbHeight = thumb;
                NavThumb.Height = thumb;
            }
            var travel = Math.Max(0, track - thumb);
            var span = NavScroll.ScrollableHeight;
            navThumbVisual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, (float)(span <= 0 ? 0 : NavScroll.VerticalOffset / span * travel), 0));
        }
        FadeNavThumb(canScroll && (sidebarPointer || navThumbHold));
    }

    private void FadeNavThumb(bool show)
    {
        if (navThumbVisual is null || show == navThumbShown) return;
        navThumbShown = show;
        var target = show ? 1f : 0f;
        if (!sidebarAnimations)
        {
            navThumbVisual.Opacity = target;
            return;
        }
        var compositor = navThumbVisual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(show ? 180 : 280);
        fade.InsertKeyFrame(1, target, compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.215f, 0.61f), new System.Numerics.Vector2(0.355f, 1f)));
        navThumbVisual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// The sidebar is as wide as it opens from the moment it is hovered, and a clip that follows <c>Reveal</c> decides how much of it shows.
    /// The clip and the edge line are animated by the compositor, so opening and closing never relayout the sidebar and keep their pace when the page behind is busy.
    /// </summary>
    private void PrepareSidebar()
    {
        var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Sidebar).Compositor;
        var sidebar = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Sidebar);
        sidebarState = compositor.CreatePropertySet();
        sidebarState.InsertScalar("Reveal", (float)AdaptiveLayout.RailWidth);

        var clip = compositor.CreateInsetClip();
        sidebar.Clip = clip;
        var inset = compositor.CreateExpressionAnimation("Max(0, sidebar.Size.X - state.Reveal)");
        inset.SetReferenceParameter("sidebar", sidebar);
        inset.SetReferenceParameter("state", sidebarState);
        clip.StartAnimation("RightInset", inset);

        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(SidebarEdge, true);
        var edge = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SidebarEdge);
        var follow = compositor.CreateExpressionAnimation("state.Reveal - 1");
        follow.SetReferenceParameter("state", sidebarState);
        edge.StartAnimation("Translation.X", follow);

        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) => { if (e.Key is VirtualKey.Tab or VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right) keyboardUsed = true; }), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => keyboardUsed = false), true);
    }

    private void UpdateSidebar()
    {
        var wanted = sidebarPointer || sidebarKeyboard || sidebarMenu;
        if (sidebarTimer is null)
        {
            sidebarTimer = DispatcherQueue.CreateTimer();
            sidebarTimer.IsRepeating = false;
            sidebarTimer.Tick += (_, _) => SetSidebar(sidebarPointer || sidebarKeyboard || sidebarMenu);
        }
        sidebarTimer.Stop();
        if (wanted == sidebarOpen) return;
        sidebarTimer.Interval = TimeSpan.FromMilliseconds(wanted ? 30 : 200);
        sidebarTimer.Start();
    }

    private void SetSidebar(bool open)
    {
        if (open == sidebarOpen || sidebarState is null) return;
        sidebarOpen = open;
        // The width changes once, while the clip still shows only the rail (opening) or has just finished hiding the rest (closing).
        // On a narrower window the page moves aside with that width, and stays there until the close animation has covered the labels.
        if (open)
        {
            Sidebar.Width = AdaptiveLayout.OpenWidth;
            sidebarReserved = true;
            ApplyResponsiveLayout();
        }
        var target = (float)(open ? AdaptiveLayout.OpenWidth : AdaptiveLayout.RailWidth);
        if (!sidebarAnimations)
        {
            sidebarState.InsertScalar("Reveal", target);
            if (!open) CollapseSidebar();
            return;
        }
        var compositor = sidebarState.Compositor;
        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.Duration = TimeSpan.FromMilliseconds(open ? 160 : 130);
        slide.InsertKeyFrame(1, target, compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.215f, 0.61f), new System.Numerics.Vector2(0.355f, 1f)));
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        sidebarState.StartAnimation("Reveal", slide);
        batch.End();
        // Closed again only once the clip has covered everything past the rail, and not if the pointer came back in the meantime.
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!sidebarOpen) CollapseSidebar(); });
    }

    private void CollapseSidebar()
    {
        Sidebar.Width = AdaptiveLayout.RailWidth;
        sidebarReserved = false;
        ApplyResponsiveLayout();
    }

    private UIElement PageElement(Page value) => value switch
    {
        Page.Install => InstallPage, Page.Installed => InstalledPage, Page.Scout => ScoutPage, Page.Cleanup => CleanupPage, Page.Paths => PathsPage, Page.Repair => RepairPage, Page.Startup => StartupPage, Page.Quiet => QuietPage, Page.Tune => TunePage, Page.Debloat => DebloatPage, Page.Fixes => FixesPage, Page.Unstick => UnstickPage, Page.Lab => LabPage, Page.Queue => QueuePage, Page.Logs => LogsPage, Page.Settings => SettingsPage, _ => OverviewPage
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
        if (!SystemUi.AnimationsEnabled) { element.Opacity = 1; element.Translation = default; element.Visibility = Visibility.Visible; return; }
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

    private void SystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args) => Post(() =>
    {
        if (appearance == Appearance.System) ApplyAppearance();
        OutputPalette.Refresh();
    });
    private void ApplyAppearance()
    {
        Root.RequestedTheme = AppearanceSettings.ThemeFor(appearance, SystemUi.Settings);
        SystemThemeItem.IsChecked = appearance == Appearance.System;
        LightThemeItem.IsChecked = appearance == Appearance.Light;
        DarkThemeItem.IsChecked = appearance == Appearance.Dark;
        UpdateThemeChrome();
    }
    private ElementTheme chromeTheme = ElementTheme.Default;
    private Brush navSelected = BrushCache.Transparent, navPrimary = BrushCache.Transparent, navMuted = BrushCache.Transparent, navAccent = BrushCache.Transparent;
    private void UpdateNavSelection()
    {
        foreach (var (button, target) in new[] { (OverviewNav, Page.Overview), (InstallNav, Page.Install), (InstalledNav, Page.Installed), (ScoutNav, Page.Scout), (CleanupNav, Page.Cleanup), (PathsNav, Page.Paths), (RepairNav, Page.Repair), (StartupNav, Page.Startup), (QuietNav, Page.Quiet), (TuneNav, Page.Tune), (DebloatNav, Page.Debloat), (FixesNav, Page.Fixes), (UnstickNav, Page.Unstick), (LabNav, Page.Lab), (QueueButton, Page.Queue), (LogsNav, Page.Logs), (SettingsNav, Page.Settings) })
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
        IconService.SetDark(!light);
        navSelected = BrushCache.Rgb(light ? 0xEBEBEB : 0x1F1F1F);
        navPrimary = BrushCache.Rgb(light ? 0x171717 : 0xFFFFFF);
        navMuted = BrushCache.Rgb(light ? 0x626262 : 0xA9A9A9);
        // Windows' own default accent stands in on an installation that will not say what its accent is.
        var usualAccent = light ? ColorHelper.FromArgb(255, 0x00, 0x78, 0xD4) : ColorHelper.FromArgb(255, 0x4C, 0xC2, 0xFF);
        navAccent = new SolidColorBrush(SystemUi.Color(light ? Windows.UI.ViewManagement.UIColorType.Accent : Windows.UI.ViewManagement.UIColorType.AccentLight2, usualAccent));
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

    private void RepairClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Repair);
        // Reading the installed-programs list takes well under a second, so the first visit scans at once.
        repair.RefreshHistory();
        if (!repair.Scanned && !repair.Busy) _ = repair.ScanAsync();
    }

    private void StartupClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Startup);
        startup.RefreshChanges();
        if (!startup.Scanned && !startup.Busy) _ = startup.ScanAsync();
    }

    private void QuietClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Quiet);
        quiet.RefreshChanges();
        if (!quiet.Scanned && !quiet.Busy) _ = quiet.ScanAsync();
    }

    private void FixesClick(object sender, RoutedEventArgs e) => ShowPage(Page.Fixes);

    private void UnstickClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Unstick);
        _ = unstick.LoadAsync();
    }

    private void LabClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Lab);
        _ = lab.LoadAsync();
    }

    private void TuneClick(object sender, RoutedEventArgs e) => OpenTune();

    private void OpenTune()
    {
        ShowPage(Page.Tune);
        tune.RefreshChanges();
        // Reading a few dozen registry values is instant, so the first visit scans at once.
        if (!tune.Scanned && !tune.Busy) _ = tune.ScanAsync();
    }

    private void DebloatClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Debloat);
        debloat.RefreshRemoved();
        // An app can be uninstalled from Start or Settings while this page sits in the background, so every visit reads the PC again.
        // The first visit shows a loading screen; after that the list is updated in place, without losing the person's place or ticks.
        if (!debloat.Busy) _ = debloat.ScanAsync();
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

    /// <summary>Our own window first: suggestions and search, with File Explorer one button away for anything it did not find.</summary>
    private async Task<string?> ChooseFolderAsync(PathSnapshot snapshot)
    {
        try
        {
            using var finder = new AddFolderViewModel(pathFinder, snapshot);
            var dialog = new AddFolderDialog(finder, PickFolderAsync) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme };
            await ShowDialogAsync(dialog);
            return dialog.Chosen;
        }
        catch (Exception ex)
        {
            // A window that cannot open must not take the app down; the plain folder picker still works.
            log.Write(ActivityLevel.Warning, ActivityArea.App, "The folder search window could not open", ex.ToString());
            return await PickFolderAsync();
        }
    }

    // Shortcuts and junctions are skipped so a folder that points back at its own parent cannot send the search round in circles.
    private static readonly EnumerationOptions Walk = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };

    private static IEnumerable<string> SafeFolders(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder, "*", Walk).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*", Walk).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
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

    private void ScoutClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        ShowPage(Page.Scout);
        await RefreshScoutBackendAsync();
        if (initialized && page == Page.Scout) ScoutSearchBox.Focus(FocusState.Programmatic);
    }, "opening Scout");
    private async Task RefreshScoutBackendAsync()
    {
        var wasReady = scout.Indexed;
        await scout.CheckBackendAsync();
        if (initialized && !wasReady && scout.Indexed && page == Page.Scout) await SearchScoutAsync(false);
    }
    private string? ScoutRoot => ScoutLocation.SelectedIndex switch { 1 => null, >= 2 => scoutFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _ => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    private ScoutItemKind ScoutKind => (ScoutItemKind)Math.Clamp(ScoutType.SelectedIndex, 0, 2);
    private Task SearchScoutAsync(bool debounce = true) => scout.SearchAsync(ScoutSearchBox.Text, ScoutRoot, ScoutKind, debounce, ScoutLocation.SelectedIndex >= 2);
    private void ScoutTextChanged(object sender, TextChangedEventArgs e) { if (initialized) Run(() => SearchScoutAsync(), "searching for files"); }
    private void ScoutTypeChanged(object sender, SelectionChangedEventArgs e) { if (initialized) Run(() => SearchScoutAsync(false), "searching for files"); }
    private void ScoutLocationChanged(object sender, SelectionChangedEventArgs e) { if (initialized) Run(ScoutLocationChangedAsync, "choosing where to search"); }
    private async Task ScoutLocationChangedAsync()
    {
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
    private void ScoutRefreshClick(object sender, RoutedEventArgs e) => Run(async () =>
    { scout.Cancel(); scout.RefreshFolder(); await scout.CheckBackendAsync(); await SearchScoutAsync(false); }, "refreshing the search index");
    private void ScoutCancelClick(object sender, RoutedEventArgs e) => scout.Cancel();
    private void ScoutSetupClick(object sender, RoutedEventArgs e) => Run(ScoutSetupAsync, "setting up instant search");
    private async Task ScoutSetupAsync()
    {
        if (!scout.CanSetup) return;
        await RefreshScoutBackendAsync();
        if (!initialized || !scout.NeedsInstallation || !scout.CanSetup) return;
        if (!model.CanQueue) return;
        var manager = model.Managers.FirstOrDefault(x => x.Status?.Health == ManagerHealth.Ready && x.Definition.Kind is ManagerKind.Winget or ManagerKind.Chocolatey);
        if (manager is null) { ShowPage(Page.Overview); return; }
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Enable instant search?", Content = "This installs Everything, a local filename index, using your package manager. Its Windows service enables fast NTFS indexing. Windows may ask for administrator permission. Folder search remains available without it.", PrimaryButtonText = "Queue installation", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
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
    private void InstalledClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        ShowPage(Page.Installed);
        if (installations.NeedsRefresh) await installations.RefreshAsync();
    }, "loading the installed apps");
    private void InstalledRefreshClick(object sender, RoutedEventArgs e) => Run(() => installations.RefreshAsync(), "refreshing the installed apps");
    private void InstalledFilterChanged(object sender, RoutedEventArgs e)
    { if (initialized) installations.Filter(InstalledSearch.Text, InstalledFilter.SelectedIndex == 1); }
    private void UpdateInstalledClick(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: InstalledRow row }) installations.QueueAction(row, PackageAction.Update); }
    private void SelectAllInstalledClick(object sender, RoutedEventArgs e) => installations.SelectAll(installations.AllSelected != true);
    private void UpdateSelectedClick(object sender, RoutedEventArgs e) => installations.QueueSelected(PackageAction.Update);
    private void UninstallSelectedClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        var rows = installations.Selected.ToArray();
        if (rows.Length == 0) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Uninstall {rows.Length} apps?",
            Content = new ScrollViewer { MaxHeight = 300, Content = new TextBlock { Text = "Save your work and close these apps. Each will be uninstalled in order.\n\n" + string.Join("\n", rows.Select(x => $"{x.Name} · {x.Source}")), TextWrapping = TextWrapping.Wrap } },
            PrimaryButtonText = "Queue uninstalls", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            operationQueue.Enqueue(rows.Where(x => x.CanAct).Select(x => PackageOperation.Manage(x.Package, PackageAction.Uninstall)));
    }, "queueing the uninstalls");
    private void RemoveQueuedClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: QueueRow row }) operationQueue.Cancel(row); }
    private void StopRunningClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (sender is not FrameworkElement { DataContext: QueueRow row } || row.Entry.State != QueueItemState.Running) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Stop {row.Name}?",
            Content = new TextBlock { Text = "TidyWindow will end the running installer. An install or update that is stopped partway can leave the app incomplete or broken, so do this only if it has stopped responding.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = "Stop it", CloseButtonText = "Keep running", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) operationQueue.Stop(row);
    }, "stopping the operation");
    private void ClearWaitingClick(object sender, RoutedEventArgs e) => operationQueue.CancelWaiting();
    private void ClearFinishedQueueClick(object sender, RoutedEventArgs e) => operationQueue.ClearFinished();
    private void QueueClick(object sender, RoutedEventArgs e) => ShowPage(Page.Queue);
    private void QueueRowClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: QueueRow row }) row.Toggle(); }
    private sealed class OutputScrollState { public double Offset = double.NaN; public bool Follow = true; }
    // Kept by queue row rather than by ScrollViewer: a list control reuses its containers, and a viewer's own state would carry one row's "stopped following" over to the next.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, OutputScrollState> outputScroll = new();
    private OutputScrollState OutputScroll(FrameworkElement element) => outputScroll.GetOrCreateValue(element.DataContext ?? element);
    private void QueueOutputViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || e.IsIntermediate) return;
        var state = OutputScroll(scroll);
        // The first position seen is the baseline (a reused viewer arrives wherever the last row left it). After that, content growing is not a
        // request to stop following; only a changed scroll position is.
        if (double.IsNaN(state.Offset)) { state.Offset = scroll.VerticalOffset; return; }
        if (Math.Abs(scroll.VerticalOffset - state.Offset) > 0.5) state.Follow = scroll.ScrollableHeight - scroll.VerticalOffset < 24;
        state.Offset = scroll.VerticalOffset;
    }
    private void QueueOutputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DependencyObject? parent = sender as DependencyObject;
        while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not ScrollViewer scroll) return;
        var state = OutputScroll(scroll);
        if (!state.Follow) return;
        DispatcherQueue.TryEnqueue(() => { if (state.Follow) scroll.ChangeView(null, scroll.ScrollableHeight, null, true); });
    }
    private void QueueStateChanged() => Run(QueueStateChangedAsync, "catching up after the queue finished");
    private async Task QueueStateChangedAsync()
    {
        scout.SetInstallationPending(operationQueue.Snapshot.Entries.Any(x => x.State is QueueItemState.Waiting or QueueItemState.Running
            && x.Operation.Kind == PackageOperationKind.Install
            && (x.Operation.Id.Equals("voidtools.Everything", StringComparison.OrdinalIgnoreCase) || x.Operation.Manager == ManagerKind.Chocolatey && x.Operation.Id.Equals("everything", StringComparison.OrdinalIgnoreCase))));
        if (operationQueue.HasWork || operationQueue.Snapshot.Completions <= reconciledCompletions) return;
        reconciledCompletions = operationQueue.Snapshot.Completions;
        // Quietly: the person may already be queueing the next job, so nothing is covered or locked while this catches up.
        try { await RefreshScoutBackendAsync(); await Refresh(quiet: true); if (page == Page.Installed) await installations.RefreshAsync(); }
        catch (Exception ex) { log.Write(ActivityLevel.Warning, ActivityArea.Install, "Package status could not refresh after the queue finished", ex.Message); }
    }
    private void UninstallInstalledClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (sender is not FrameworkElement { DataContext: InstalledRow row } || !row.CanAct) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Uninstall {row.Name}?",
            Content = new TextBlock { Text = $"This removes {row.Name} using {row.Package.ManagerName}{(row.Package.Global ? " for all users" : "")}. Save your work and close the app first.\n\nThe app’s uninstaller may ask for administrator permission. Personal settings are handled by the uninstaller; Scoop’s persistent data is kept.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) installations.QueueAction(row, PackageAction.Uninstall);
    }, "queueing the uninstall");
    private void LogsClick(object sender, RoutedEventArgs e) => ShowPage(Page.Logs);
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Settings);
        _ = settings.EnsureLoadedAsync();
    }

    private void ReportProblem(string key, string title, string detail) =>
        toasts.Show(key, ToastKind.Warning, title, ToastCenter.Brief(detail), "Open Logs", () => { ShowPage(Page.Logs); return Task.CompletedTask; }, 15);

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
    private void CopyLogsClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (logs.Rows.Count == 0) return;
        var ok = TryCopy(ActivityLog.Format(logs.Visible));
        CopyLogsLabel.Text = ok ? "Copied" : "Clipboard busy";
        await Task.Delay(1500);
        CopyLogsLabel.Text = "Copy";
    }, "copying the log");
    private void CopyEntryClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (sender is not FrameworkElement { DataContext: LogRow row } || !TryCopy(ActivityLog.Format([row.Entry]))) return;
        row.SetCopied(true);
        await Task.Delay(1500);
        row.SetCopied(false);
    }, "copying the entry");
    private void OpenLogsFolderClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (log.Folder is null) return;
        try { Directory.CreateDirectory(log.Folder); await Launcher.LaunchFolderPathAsync(log.Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { log.Write(ActivityLevel.Warning, ActivityArea.App, "The log folder could not be opened", ex.Message); }
    }, "opening the log folder");
    private void ClearLogsClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (log.Count == 0) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Clear all logs?", Content = "This deletes every saved entry from this computer. It does not undo anything TidyWindow has done.", PrimaryButtonText = "Clear logs", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) log.Clear();
    }, "clearing the logs");
    private ManagerKind? Filter => SourceFilter.SelectedIndex switch { 1 => ManagerKind.Winget, 2 => ManagerKind.Chocolatey, 3 => ManagerKind.Scoop, _ => null };
    private void SearchTextChanged(object sender, TextChangedEventArgs e) { if (initialized) Run(() => model.SearchAsync(SearchBox.Text, Filter), "searching for packages"); }
    private void SourceChanged(object sender, SelectionChangedEventArgs e) { if (initialized) Run(() => model.SearchAsync(SearchBox.Text, Filter, false), "searching for packages"); }
    private void SearchClick(object sender, RoutedEventArgs e) => Run(() => model.SearchAsync(SearchBox.Text, Filter, false), "searching for packages");
    private void SearchKeyDown(object sender, KeyRoutedEventArgs e)
    { if (e.Key == VirtualKey.Enter) { e.Handled = true; Run(() => model.SearchAsync(SearchBox.Text, Filter, false), "searching for packages"); } }
    private void CancelSearchClick(object sender, RoutedEventArgs e) => model.CancelSearch();
    private void RefreshClick(object sender, RoutedEventArgs e) => Run(() => Refresh(), "refreshing the package managers");
    private async Task Refresh(bool quiet = false)
    {
        await model.RefreshAsync(quiet);
        installations.NeedsRefresh = true;
        if (SearchBox.Text.Trim().Length >= 2 && !model.Searching && model.Packages.Count == 0)
            await model.SearchAsync(SearchBox.Text, Filter, false);
    }
    private void BucketsToggleClick(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ManagerRow row }) Run(() => model.ToggleBucketsAsync(row), "showing the buckets"); }

    private void AddBucketClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BucketRow bucket }) return;
        if (model.Managers.FirstOrDefault(x => x.Buckets?.Available.Contains(bucket) == true) is { } row) Run(() => model.AddBucketAsync(row, bucket), "adding the bucket");
    }

    private void ManagerRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ManagerRow row }) return;
        if (row.Status?.Health == ManagerHealth.Restricted)
            Run(() => ShowDialogAsync(new ContentDialog { Title = "This manager is restricted", Content = new TextBlock { Text = row.Detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "Close" }), "showing the manager's status");
        else Run(() => model.RecoverAsync(row), "setting up the package manager");
    }
    private void InstallPackageClick(object sender, RoutedEventArgs e) => Run(async () =>
    {
        if (sender is not FrameworkElement { DataContext: PackageRow row } || !row.CanInstall) return;
        var content = new StackPanel { Spacing = 14, MaxWidth = 480 };
        content.Children.Add(new TextBlock { Text = $"{row.Id}\n{row.Source} · available version {row.Version}", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = PackageService.DisplayInstallCommand(row.Package), FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        content.Children.Add(new TextBlock { Text = "This installs the latest version from this source and accepts the package’s license agreements. The package manager may open an installer or request administrator permission.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (row.Package.Manager == ManagerKind.Chocolatey)
            content.Children.Add(new TextBlock { Text = "Chocolatey will open in a separate administrator window. TidyWindow itself stays unelevated.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = $"Install {row.Name}?", Content = content, PrimaryButtonText = "Install package", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) model.QueueInstall(row);
    }, "queueing the install");
    private static string Doing(PackageOperation operation) => operation.Fix is not null ? "being applied"
        : operation.Kind switch { PackageOperationKind.Install => "being installed", PackageOperationKind.Update => "being updated", _ => "being uninstalled" };

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeConfirmed) return;
        var running = RunningWork();
        if (running.Count == 0) return;
        args.Cancel = true;
        if (closingDialog) return;
        closingDialog = true;
        Run(() => ConfirmCloseAsync(running), "closing TidyWindow");
    }

    /// <summary>What closing now would cut short, one line each. Empty when nothing is running.</summary>
    private List<string> RunningWork()
    {
        var what = new List<string>();
        var running = operationQueue.Running; var waiting = operationQueue.Waiting;
        if (running is not null) what.Add($"{running.Operation.Name} is {Doing(running.Operation)} right now.");
        if (waiting > 0) what.Add($"{waiting} more {(waiting == 1 ? "item is" : "items are")} waiting in the queue.");
        if (what.Count == 0 && model.Busy) what.Add("A package manager task is in progress.");
        // The pages that change the PC, by the state each exposes. A page whose first scan is still running is left alone: a scan only reads.
        if (cleanup.CleaningVisibility == Visibility.Visible) what.Add("Cleanup is still cleaning.");
        if (repair.FixOverlay) what.Add("Repair is still fixing a program.");
        if (debloat.WorkOverlay && !debloat.ScanOverlay) what.Add("Debloat is still removing an app or putting one back.");
        if (startup.Busy && !startup.ScanOverlay) what.Add("Startup is still working on a change.");
        if (quiet.Busy && !quiet.ScanOverlay) what.Add("Quiet is still working on a change.");
        if (tune.Busy && !tune.ScanOverlay) what.Add("Tune is still working on a change.");
        if (paths.Busy && !paths.ScanOverlay) what.Add("Path Doctor is still working on your PATH.");
        if (unstick.Sections.Any(section => section.Rows.Any(row => row.Busy))) what.Add("An Unstick action is still running.");
        if (lab.Running) what.Add("Lab is still running its check.");
        if (settings.ProgressVisibility == Visibility.Visible) what.Add("The update is still downloading.");
        return what;
    }

    private async Task ConfirmCloseAsync(List<string> what)
    {
        try
        {
            // A confirmation that is up steps aside, its handler seeing None, so the one question that matters now can be asked.
            if (openDialog is { } open)
            {
                open.Hide();
                try { if (openDialogShowing is { } showing) await showing; }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            }
            var packages = operationQueue.HasWork || model.Busy;
            var dialog = new ContentDialog
            {
                Title = "Work is still running",
                Content = new TextBlock
                {
                    Text = string.Join(" ", what) + "\n\nIf you close TidyWindow now, this work is stopped partway. " + (packages
                        ? "An install or update that is cut off can leave an app broken or incomplete, and anything waiting in the queue is dropped. "
                        : "A repair or cleanup that is cut off can be left half done, and may not be recorded for undo. ") + "Closing now is not recommended.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 440
                },
                CloseButtonText = "Keep open", SecondaryButtonText = "Close anyway", DefaultButton = ContentDialogButton.Close
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Secondary) return;
            closeConfirmed = true;
            toasts.Show("closing", ToastKind.Info, "Stopping work before closing…", "", "", null, 10);
            // Ending the running command takes a moment; it is given a short time, and a command that will not end cannot keep the window open.
            try { await operationQueue.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { }
            catch (Exception ex) { log.Write(ActivityLevel.Warning, ActivityArea.App, "The queue could not be stopped cleanly before closing", ex.ToString()); }
            Close();
        }
        finally { closingDialog = false; }
    }
}


