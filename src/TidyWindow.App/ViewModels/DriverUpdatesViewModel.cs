using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Updates;

namespace TidyWindow.App.ViewModels;

public sealed partial class DriverUpdatesViewModel : ViewModelBase
{
    private readonly DriverUpdateService _driverUpdateService;
    private readonly MainViewModel _mainViewModel;
    private DateTimeOffset? _lastChecked;
    private const int OperationLogLimit = 12;
    private const int MaxHealthInsights = 10;
    private const string DisplayAdapterClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly IReadOnlyDictionary<string, GpuVendorGuidanceDescriptor> GpuGuidanceCatalog = new Dictionary<string, GpuVendorGuidanceDescriptor>(StringComparer.OrdinalIgnoreCase)
    {
        ["AMD"] = new(
            "AMD Radeon",
            "AMD releases Adrenalin packages more frequently than Windows Update. Use AMD Auto-Detect to pull the latest display and chipset drivers directly.",
            "Open AMD Auto-Detect",
            new Uri("https://www.amd.com/en/support")),
        ["NVIDIA"] = new(
            "NVIDIA GeForce",
            "NVIDIA publishes Game Ready and Studio drivers via GeForce Experience. Launch their download center to stay ahead of Windows Update.",
            "Open NVIDIA Drivers",
            new Uri("https://www.nvidia.com/Download/index.aspx")),
        ["Intel"] = new(
            "Intel Arc / Iris / UHD",
            "Intel Driver & Support Assistant keeps GPU and chipset packages current, including quarterly Arc releases.",
            "Open Intel DSA",
            new Uri("https://www.intel.com/content/www/us/en/support/detect.html"))
    };

    public DriverUpdatesViewModel(DriverUpdateService driverUpdateService, MainViewModel mainViewModel)
    {
        _driverUpdateService = driverUpdateService ?? throw new ArgumentNullException(nameof(driverUpdateService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        Updates = new ObservableCollection<DriverUpdateItemViewModel>();
        Updates.CollectionChanged += OnUpdatesCollectionChanged;

        Warnings = new ObservableCollection<string>();
        Warnings.CollectionChanged += OnWarningsCollectionChanged;

        InstalledDrivers = new ObservableCollection<InstalledDriverItemViewModel>();
        InstalledDrivers.CollectionChanged += OnInstalledDriversCollectionChanged;

        SkipDetails = new ObservableCollection<string>();
        SkipDetails.CollectionChanged += OnSkipDetailsCollectionChanged;

        SkipSummaries = new ObservableCollection<DriverSkipSummaryViewModel>();
        SkipSummaries.CollectionChanged += OnSkipSummariesCollectionChanged;

        InstallSummaries = new ObservableCollection<DriverUpdateInstallSummary>();
        InstallSummaries.CollectionChanged += OnInstallSummariesCollectionChanged;

        OperationMessages = new ObservableCollection<string>();
        OperationMessages.CollectionChanged += OnOperationMessagesCollectionChanged;

        HealthInsights = new ObservableCollection<DriverHealthInsightViewModel>();
        HealthInsights.CollectionChanged += OnHealthInsightsCollectionChanged;

        GpuGuidance = new ObservableCollection<GpuVendorGuidanceViewModel>();
        GpuGuidance.CollectionChanged += OnGpuGuidanceCollectionChanged;

        Summary = "Scan Windows Update for driver version differences.";
    }

    public ObservableCollection<DriverUpdateItemViewModel> Updates { get; }

    public ObservableCollection<string> Warnings { get; }

    public ObservableCollection<InstalledDriverItemViewModel> InstalledDrivers { get; }

    public ObservableCollection<string> SkipDetails { get; }

    public ObservableCollection<DriverSkipSummaryViewModel> SkipSummaries { get; }

    public ObservableCollection<DriverUpdateInstallSummary> InstallSummaries { get; }

    public ObservableCollection<string> OperationMessages { get; }

    public ObservableCollection<DriverHealthInsightViewModel> HealthInsights { get; }

    public ObservableCollection<GpuVendorGuidanceViewModel> GpuGuidance { get; }

    public bool HasResults => Updates.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasInstalledDrivers => InstalledDrivers.Count > 0;

    public bool HasSkipDetails => SkipDetails.Count > 0;

    public bool HasSkipSummaries => SkipSummaries.Count > 0;

    public bool HasInstallSummaries => InstallSummaries.Count > 0;

    public bool HasOperationMessages => OperationMessages.Count > 0;

    public bool HasInstallableUpdates => Updates.Any(static update => update.CanInstall);

    public bool HasMaintainableDrivers => InstalledDrivers.Any(static driver => driver.CanMaintain);

    public bool HasHealthInsights => HealthInsights.Count > 0;

    public bool HasGpuGuidance => GpuGuidance.Count > 0;

    public DateTimeOffset? LastChecked
    {
        get => _lastChecked;
        private set
        {
            if (SetProperty(ref _lastChecked, value))
            {
                OnPropertyChanged(nameof(LastCheckedDisplay));
            }
        }
    }

    public string LastCheckedDisplay => LastChecked is DateTimeOffset timestamp
        ? $"Last checked {timestamp.LocalDateTime:G}"
        : "No scans yet.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _includeOptional;

    [ObservableProperty]
    private string _summary;

    [ObservableProperty]
    private DriverUpdateItemViewModel? _selectedUpdate;

    [ObservableProperty]
    private string? _activeFiltersSummary;

    [ObservableProperty]
    private bool _isInstallInProgress;

    [ObservableProperty]
    private bool _isMaintenanceInProgress;

    public bool HasScanned { get; private set; }

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(ActiveFiltersSummary);

    public bool IsDriverOperationInProgress => IsBusy || IsInstallInProgress || IsMaintenanceInProgress;

    public bool CanRunDriverOperations => !IsDriverOperationInProgress;

    public string InstalledDriversSummary => InstalledDrivers.Count switch
    {
        0 => "No installed driver entries were cataloged.",
        1 => "Cataloged 1 installed driver entry.",
        _ => $"Cataloged {InstalledDrivers.Count} installed driver entries."
    };

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            HasScanned = true;
            _mainViewModel.SetStatusMessage("Checking for driver updates...");

            Warnings.Clear();
            SkipDetails.Clear();
            SkipSummaries.Clear();
            InstalledDrivers.Clear();
            HealthInsights.Clear();
            GpuGuidance.Clear();

            var result = await _driverUpdateService.DetectAsync(IncludeOptional);

            Updates.Clear();
            foreach (var info in result.Updates)
            {
                Updates.Add(new DriverUpdateItemViewModel(info));
            }

            foreach (var driver in result.InstalledDrivers)
            {
                InstalledDrivers.Add(new InstalledDriverItemViewModel(driver));
            }

            LastChecked = result.GeneratedAt;
            Summary = BuildSummary(result);
            ActiveFiltersSummary = BuildFilterSummary(result.Filters);

            foreach (var warning in result.Warnings)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    Warnings.Add(warning);
                }
            }

            foreach (var detail in result.SkipDetails)
            {
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    SkipDetails.Add(detail);
                }
            }

            foreach (var summary in result.SkipSummaries)
            {
                SkipSummaries.Add(new DriverSkipSummaryViewModel(summary));
            }

            RebuildDriverInsights(result);

            var actionable = result.Updates.Count(update => update.Status == DriverUpdateStatus.UpdateAvailable);
            if (result.Updates.Count == 0)
            {
                _mainViewModel.SetStatusMessage("No driver updates were offered.");
            }
            else if (actionable == 0)
            {
                _mainViewModel.SetStatusMessage($"Checked {result.Updates.Count} driver update(s); all current.");
            }
            else
            {
                _mainViewModel.SetStatusMessage($"Detected {actionable} driver update(s) requiring attention.");
            }
        }
        catch (Exception ex)
        {
            Warnings.Add(ex.Message);
            _mainViewModel.SetStatusMessage($"Driver scan failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildSummary(DriverUpdateScanResult result)
    {
        var updates = result.Updates;

        if (updates.Count == 0)
        {
            return result.InstalledDrivers.Count > 0
                ? $"No driver updates detected. {InstalledDriversSummary}"
                : "No driver updates detected.";
        }

        var actionable = updates.Count(update => update.Status == DriverUpdateStatus.UpdateAvailable);
        if (actionable == 0)
        {
            return updates.Count == 1
                ? "1 driver update is already at the latest version."
                : $"{updates.Count} driver updates are already at the latest version.";
        }

        return actionable == 1
            ? "1 driver requires an update."
            : $"{actionable} drivers require updates.";
    }

    [RelayCommand]
    private async Task InstallPendingUpdatesAsync()
    {
        if (!CanRunDriverOperations)
        {
            return;
        }

        var installable = Updates
            .Where(static update => update.CanInstall)
            .ToList();

        if (installable.Count == 0)
        {
            _mainViewModel.SetStatusMessage("No driver updates are available to install.");
            return;
        }

        await ExecuteInstallAsync(installable, BuildDriverCountLabel(installable.Count)).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task InstallUpdateAsync(DriverUpdateItemViewModel? update)
    {
        if (!CanRunDriverOperations)
        {
            return;
        }

        if (update is null)
        {
            _mainViewModel.SetStatusMessage("Select a driver update to install.");
            return;
        }

        if (!update.CanInstall)
        {
            _mainViewModel.SetStatusMessage("The selected driver is not available via Windows Update.");
            return;
        }

        await ExecuteInstallAsync(new[] { update }, update.DeviceName).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ReinstallDriverAsync(object? parameter)
    {
        if (!CanRunDriverOperations)
        {
            return;
        }

        var (infReference, label) = ResolveMaintenanceContext(parameter);
        if (string.IsNullOrWhiteSpace(infReference))
        {
            _mainViewModel.SetStatusMessage("No driver package reference is available to reinstall.");
            return;
        }

        await ExecuteMaintenanceAsync(
            () => _driverUpdateService.ReinstallDriverAsync(infReference),
            $"Reinstalling {label} via pnputil...",
            label).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RollbackDriverAsync(object? parameter)
    {
        if (!CanRunDriverOperations)
        {
            return;
        }

        var (infReference, label) = ResolveMaintenanceContext(parameter);
        if (string.IsNullOrWhiteSpace(infReference))
        {
            _mainViewModel.SetStatusMessage("No driver package reference is available to roll back.");
            return;
        }

        await ExecuteMaintenanceAsync(
            () => _driverUpdateService.RollbackDriverAsync(infReference),
            $"Rolling back {label} via pnputil...",
            label).ConfigureAwait(false);
    }

    private string? BuildFilterSummary(DriverFilterSummary? filters)
    {
        if (filters is null)
        {
            return null;
        }

        var segments = new List<string>();

        if (filters.IncludeDriverClasses.Count > 0)
        {
            segments.Add($"Include classes: {string.Join(", ", filters.IncludeDriverClasses)}");
        }

        if (filters.ExcludeDriverClasses.Count > 0)
        {
            segments.Add($"Exclude classes: {string.Join(", ", filters.ExcludeDriverClasses)}");
        }

        if (filters.AllowVendors.Count > 0)
        {
            segments.Add($"Allowed vendors: {string.Join(", ", filters.AllowVendors)}");
        }

        if (filters.BlockVendors.Count > 0)
        {
            segments.Add($"Blocked vendors: {string.Join(", ", filters.BlockVendors)}");
        }

        return segments.Count == 0 ? null : string.Join(" | ", segments);
    }

    private void RebuildDriverInsights(DriverUpdateScanResult result)
    {
        HealthInsights.Clear();
        GpuGuidance.Clear();

        var healthInsights = BuildHealthInsights(result.InstalledDrivers);
        foreach (var insight in healthInsights)
        {
            HealthInsights.Add(new DriverHealthInsightViewModel(insight));
        }

        var gpuGuidance = BuildGpuGuidance(result.InstalledDrivers, result.Updates);
        foreach (var guidance in gpuGuidance)
        {
            GpuGuidance.Add(new GpuVendorGuidanceViewModel(guidance));
        }
    }

    private static IReadOnlyList<DriverHealthInsight> BuildHealthInsights(IReadOnlyList<InstalledDriverInfo> installedDrivers)
    {
        if (installedDrivers is null || installedDrivers.Count == 0)
        {
            return Array.Empty<DriverHealthInsight>();
        }

        var buffer = new List<DriverHealthInsight>();

        foreach (var driver in installedDrivers)
        {
            if (driver is null)
            {
                continue;
            }

            if (driver.ProblemCode is int problemCode && problemCode > 0)
            {
                var issue = $"Problem code {problemCode}";
                var detail = string.IsNullOrWhiteSpace(driver.Status) ? "Device reported an issue." : driver.Status;
                buffer.Add(new DriverHealthInsight(driver.DeviceName, issue, detail, driver.InfName, DriverHealthSeverity.Warning));
                continue;
            }

            if (driver.IsSigned is false)
            {
                buffer.Add(new DriverHealthInsight(driver.DeviceName, "Driver signature missing", "Unsigned drivers may fail to load or pass Smart App Control.", driver.InfName, DriverHealthSeverity.Advisory));
            }
        }

        if (buffer.Count == 0)
        {
            return Array.Empty<DriverHealthInsight>();
        }

        return buffer
            .OrderByDescending(static insight => insight.Severity)
            .ThenBy(static insight => insight.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxHealthInsights)
            .ToArray();
    }

    private static IReadOnlyList<GpuVendorGuidance> BuildGpuGuidance(IReadOnlyList<InstalledDriverInfo> installedDrivers, IReadOnlyList<DriverUpdateInfo> updateCandidates)
    {
        var vendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void CaptureVendor(string? manufacturer, string? normalizedVendor, string? driverClass, string? classGuid, ISet<string> sink)
        {
            if (!IsDisplayDriver(driverClass, classGuid))
            {
                return;
            }

            var vendor = NormalizeGpuVendor(manufacturer) ?? NormalizeGpuVendor(normalizedVendor);
            if (!string.IsNullOrWhiteSpace(vendor))
            {
                sink.Add(vendor!);
            }
        }

        if (installedDrivers is not null)
        {
            foreach (var driver in installedDrivers)
            {
                if (driver is null)
                {
                    continue;
                }

                CaptureVendor(driver.Manufacturer, null, null, driver.ClassGuid, vendors);
            }
        }

        if (updateCandidates is not null)
        {
            foreach (var update in updateCandidates)
            {
                if (update is null)
                {
                    continue;
                }

                CaptureVendor(update.Manufacturer, update.NormalizedVendor, update.DriverClass, null, vendors);
            }
        }

        if (vendors.Count == 0)
        {
            return Array.Empty<GpuVendorGuidance>();
        }

        var guidance = new List<GpuVendorGuidance>();

        foreach (var vendor in vendors)
        {
            if (GpuGuidanceCatalog.TryGetValue(vendor, out var descriptor))
            {
                guidance.Add(new GpuVendorGuidance(vendor, descriptor.VendorLabel, descriptor.Message, descriptor.LinkLabel, descriptor.SupportUri));
            }
        }

        return guidance.Count == 0 ? Array.Empty<GpuVendorGuidance>() : guidance;
    }

    private static bool IsDisplayDriver(string? driverClass, string? classGuid)
    {
        if (!string.IsNullOrWhiteSpace(driverClass) && driverClass.Contains("display", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(classGuid) && classGuid.Equals(DisplayAdapterClassGuid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? NormalizeGpuVendor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("RADEON", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ADVANCED MICRO DEVICES", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GEFORCE", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (text.Contains("INTEL", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return null;
    }

    private async Task ExecuteInstallAsync(IReadOnlyCollection<DriverUpdateItemViewModel> drivers, string contextDescription)
    {
        if (drivers is null || drivers.Count == 0)
        {
            _mainViewModel.SetStatusMessage("Select at least one driver update to install.");
            return;
        }

        var requests = drivers
            .Where(static driver => driver.CanInstall)
            .Select(static driver => new DriverUpdateInstallRequest(driver.UpdateId!, driver.Title))
            .ToArray();

        if (requests.Length == 0)
        {
            _mainViewModel.SetStatusMessage("Unable to resolve Windows Update identifiers for the selected drivers.");
            return;
        }

        try
        {
            IsInstallInProgress = true;
            _mainViewModel.SetStatusMessage($"Installing {contextDescription} via Windows Update...");
            var result = await _driverUpdateService.InstallUpdatesAsync(requests).ConfigureAwait(false);
            ApplyInstallResult(result, contextDescription);
        }
        catch (Exception ex)
        {
            AppendOperationMessage($"Driver install failed: {ex.Message}");
            _mainViewModel.SetStatusMessage($"Driver install failed: {ex.Message}");
        }
        finally
        {
            IsInstallInProgress = false;
        }
    }

    private async Task ExecuteMaintenanceAsync(Func<Task<DriverMaintenanceResult>> operation, string statusMessage, string contextLabel)
    {
        if (operation is null)
        {
            return;
        }

        try
        {
            IsMaintenanceInProgress = true;
            _mainViewModel.SetStatusMessage(statusMessage);
            var result = await operation().ConfigureAwait(false);
            ApplyMaintenanceResult(result, contextLabel);
        }
        catch (Exception ex)
        {
            AppendOperationMessage($"Driver maintenance failed: {ex.Message}");
            _mainViewModel.SetStatusMessage($"Driver maintenance failed: {ex.Message}");
        }
        finally
        {
            IsMaintenanceInProgress = false;
        }
    }

    private void ApplyInstallResult(DriverUpdateInstallResult result, string contextDescription)
    {
        InstallSummaries.Clear();
        foreach (var summary in result.Updates)
        {
            InstallSummaries.Add(summary);
        }

        var message = result.Success
            ? $"Windows Update install completed for {contextDescription}."
            : $"Windows Update install failed for {contextDescription}.";

        AppendOperationMessage(message);
        AppendOperationMessages(result.Messages);

        if (result.RebootRequired)
        {
            AppendOperationMessage("Windows reported that a restart is required to complete driver installation.");
        }

        _mainViewModel.SetStatusMessage(message);
    }

    private void ApplyMaintenanceResult(DriverMaintenanceResult result, string contextLabel)
    {
        var headline = result.Success
            ? $"{result.Operation} completed for {contextLabel}."
            : $"{result.Operation} failed for {contextLabel}.";

        AppendOperationMessage(headline);
        AppendOperationMessages(result.Messages);

        if (result.UsedFallbackPlan)
        {
            AppendOperationMessage("Used fallback pnputil plan during driver maintenance.");
        }

        _mainViewModel.SetStatusMessage(headline);
    }

    private void AppendOperationMessages(IEnumerable<string> messages)
    {
        if (messages is null)
        {
            return;
        }

        foreach (var message in messages)
        {
            AppendOperationMessage(message);
        }
    }

    private void AppendOperationMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        OperationMessages.Insert(0, message.Trim());

        while (OperationMessages.Count > OperationLogLimit)
        {
            OperationMessages.RemoveAt(OperationMessages.Count - 1);
        }
    }

    private static string BuildDriverCountLabel(int count)
    {
        if (count <= 0)
        {
            return "driver updates";
        }

        return count == 1 ? "1 driver update" : $"{count} driver updates";
    }

    private static (string? InfReference, string Label) ResolveMaintenanceContext(object? parameter)
    {
        return parameter switch
        {
            DriverUpdateItemViewModel update => (update.InstalledInfPath, update.DeviceName),
            InstalledDriverItemViewModel driver => (driver.InfName, driver.DeviceName),
            string reference when !string.IsNullOrWhiteSpace(reference) => (reference.Trim(), reference.Trim()),
            _ => (null, "selected driver")
        };
    }

    private void OnUpdatesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasInstallableUpdates));
    }

    private void OnWarningsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWarnings));
    }

    private void OnInstalledDriversCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasInstalledDrivers));
        OnPropertyChanged(nameof(InstalledDriversSummary));
        OnPropertyChanged(nameof(HasMaintainableDrivers));
    }

    private void OnSkipDetailsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSkipDetails));
    }

    private void OnSkipSummariesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSkipSummaries));
    }

    private void OnInstallSummariesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasInstallSummaries));
    }

    private void OnOperationMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasOperationMessages));
    }

    private void OnHealthInsightsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasHealthInsights));
    }

    private void OnGpuGuidanceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasGpuGuidance));
    }

    partial void OnActiveFiltersSummaryChanged(string? value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    partial void OnIsBusyChanged(bool value)
    {
        UpdateDriverOperationState();
    }

    partial void OnIsInstallInProgressChanged(bool value)
    {
        UpdateDriverOperationState();
    }

    partial void OnIsMaintenanceInProgressChanged(bool value)
    {
        UpdateDriverOperationState();
    }

    private void UpdateDriverOperationState()
    {
        OnPropertyChanged(nameof(IsDriverOperationInProgress));
        OnPropertyChanged(nameof(CanRunDriverOperations));
    }
}

public sealed partial class DriverUpdateItemViewModel : ObservableObject
{
    private readonly DriverUpdateInfo _info;

    public DriverUpdateItemViewModel(DriverUpdateInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public string Title => _info.Title;

    public string DeviceName => _info.DeviceName;

    public string? Manufacturer => _info.Manufacturer;

    public bool HasManufacturer => !string.IsNullOrWhiteSpace(Manufacturer);

    public string CurrentVersion => string.IsNullOrWhiteSpace(_info.CurrentVersion) ? "Unknown" : _info.CurrentVersion;

    public string AvailableVersion => string.IsNullOrWhiteSpace(_info.AvailableVersion) ? "Unknown" : _info.AvailableVersion;

    public string VersionSummary => $"{CurrentVersion} → {AvailableVersion}";

    public string? CurrentVersionDateDisplay => _info.CurrentVersionDate?.ToLocalTime().ToString("yyyy-MM-dd");

    public string? AvailableVersionDateDisplay => _info.AvailableVersionDate?.ToLocalTime().ToString("yyyy-MM-dd");

    public bool HasCurrentVersionTimestamp => _info.CurrentVersionDate.HasValue;

    public bool HasAvailableVersionTimestamp => _info.AvailableVersionDate.HasValue;

    public DriverUpdateStatus Status => _info.Status;

    public string StatusLabel => Status switch
    {
        DriverUpdateStatus.UpdateAvailable => "Update available",
        DriverUpdateStatus.UpToDate => "Latest installed",
        _ => "Version unknown"
    };

    public bool IsOptional => _info.IsOptional;

    public DriverUpdateBadgeHints BadgeHints => _info.BadgeHints;

    public string AvailabilityBadgeState => string.IsNullOrWhiteSpace(BadgeHints.AvailabilityState)
        ? StatusLabel
        : BadgeHints.AvailabilityState;

    public string? AvailabilityBadgeDetail => BadgeHints.AvailabilityDetail;

    public bool HasAvailabilityBadgeDetail => !string.IsNullOrWhiteSpace(AvailabilityBadgeDetail);

    public bool IsDowngradeRisk => BadgeHints.IsDowngradeRisk;

    public string? DowngradeRiskDetail => BadgeHints.DowngradeRiskDetail;

    public bool HasDowngradeRiskDetail => !string.IsNullOrWhiteSpace(DowngradeRiskDetail);

    public string VendorBadgeLabel => string.IsNullOrWhiteSpace(BadgeHints.VendorName)
        ? (HasManufacturer ? Manufacturer! : "Unknown vendor")
        : BadgeHints.VendorName!;

    public bool HasVendorBadge => !string.IsNullOrWhiteSpace(VendorBadgeLabel);

    public string DriverClassBadgeLabel => string.IsNullOrWhiteSpace(BadgeHints.DriverClassName)
        ? (string.IsNullOrWhiteSpace(DriverClass) ? "Unclassified" : DriverClass!)
        : BadgeHints.DriverClassName!;

    public bool HasDriverClassBadge => !string.IsNullOrWhiteSpace(DriverClassBadgeLabel);

    public string OptionalBadgeLabel => string.IsNullOrWhiteSpace(BadgeHints.OptionalLabel)
        ? (IsOptional ? "Optional" : "Recommended")
        : BadgeHints.OptionalLabel;

    public bool ShowOptionalBadge => BadgeHints.IsOptional;

    public string HardwareIdsDisplay => _info.HardwareIds.Count == 0
        ? "No hardware identifiers detected."
        : string.Join(Environment.NewLine, _info.HardwareIds);

    public IReadOnlyList<string> Categories => _info.Categories;

    public string CategoryDisplay => _info.Categories.Count == 0
        ? "Uncategorized"
        : string.Join(", ", _info.Categories);

    public IReadOnlyList<Uri> InformationLinks => _info.InformationLinks;

    public bool HasInformationLinks => _info.InformationLinks.Count > 0;

    public Uri? PrimaryInformationLink => HasInformationLinks ? _info.InformationLinks[0] : null;

    public string? Description => _info.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string? DriverClass => _info.DriverClass;

    public bool HasDriverClass => !string.IsNullOrWhiteSpace(DriverClass);

    public string? NormalizedVendor => _info.NormalizedVendor;

    public string? NormalizedDriverClass => _info.NormalizedDriverClass;

    public string? Classification => _info.Classification;

    public bool HasClassification => !string.IsNullOrWhiteSpace(Classification);

    public string? Severity => _info.Severity;

    public bool HasSeverity => !string.IsNullOrWhiteSpace(Severity);

    public string? UpdateId => _info.UpdateId;

    public int? RevisionNumber => _info.RevisionNumber;

    public bool CanInstall => Status == DriverUpdateStatus.UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateId);

    public bool HasDriverActions => CanInstall || HasInstalledInfPath;

    public string UpdateIdentifierDisplay => string.IsNullOrWhiteSpace(UpdateId)
        ? ""
        : RevisionNumber.HasValue
            ? $"{UpdateId} (rev {RevisionNumber})"
            : UpdateId!;

    public bool HasUpdateIdentifier => !string.IsNullOrWhiteSpace(UpdateIdentifierDisplay);

    public string? InstalledInfPath => _info.InstalledInfPath;

    public string? InstalledManufacturer => _info.InstalledManufacturer;

    public bool HasInstalledManufacturer => !string.IsNullOrWhiteSpace(InstalledManufacturer);

    public string InstalledManufacturerDisplay => HasInstalledManufacturer ? InstalledManufacturer! : "Unknown";

    public VersionComparisonStatus ComparisonStatus => _info.VersionComparison.Status;

    public string ComparisonStatusLabel => ComparisonStatus switch
    {
        VersionComparisonStatus.UpdateAvailable => "Newer version available",
        VersionComparisonStatus.PotentialDowngrade => "Potential downgrade",
        VersionComparisonStatus.Equal => "Same version",
        _ => "Comparison unavailable"
    };

    public bool IsComparisonMeaningful => ComparisonStatus != VersionComparisonStatus.Unknown;

    public string? ComparisonDetails => _info.VersionComparison.Details;

    public bool HasComparisonDetails => !string.IsNullOrWhiteSpace(ComparisonDetails);

    public bool HasInstalledInfPath => !string.IsNullOrWhiteSpace(InstalledInfPath);
}

public sealed class DriverSkipSummaryViewModel
{
    private readonly DriverUpdateSkipSummary _summary;

    public DriverSkipSummaryViewModel(DriverUpdateSkipSummary summary)
    {
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public string Title => _summary.Title;

    public string DeviceName => string.IsNullOrWhiteSpace(_summary.DeviceName) ? Title : _summary.DeviceName!;

    public string VendorDisplay => string.IsNullOrWhiteSpace(_summary.Manufacturer) ? "Unknown vendor" : _summary.Manufacturer!;

    public string DriverClassDisplay => string.IsNullOrWhiteSpace(_summary.DriverClass) ? "Unclassified" : _summary.DriverClass!;

    public string Reason => _summary.Reason;

    public string ReasonCode => _summary.ReasonCode;

    public bool IsOptional => _summary.IsOptional;

    public string PillLabel => IsOptional ? "Optional" : "Filtered";

    public string PillAutomationName => IsOptional ? "Optional update hidden" : "Policy filtered";

    public string? UpdateId => _summary.UpdateId;

    public string? NormalizedVendor => _summary.NormalizedVendor;

    public string? NormalizedDriverClass => _summary.NormalizedDriverClass;
}

public sealed class InstalledDriverItemViewModel
{
    private readonly InstalledDriverInfo _info;

    public InstalledDriverItemViewModel(InstalledDriverInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    public string DeviceName => string.IsNullOrWhiteSpace(_info.DeviceName) ? "Unknown device" : _info.DeviceName;

    public string Manufacturer => string.IsNullOrWhiteSpace(_info.Manufacturer) ? "—" : _info.Manufacturer!;

    public string Provider => string.IsNullOrWhiteSpace(_info.Provider) ? "—" : _info.Provider!;

    public string DriverVersion => string.IsNullOrWhiteSpace(_info.DriverVersion) ? "Unknown" : _info.DriverVersion!;

    public string DriverDateDisplay => _info.DriverDate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—";

    public string InstallDateDisplay => _info.InstallDate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "—";

    public string Status => string.IsNullOrWhiteSpace(_info.Status) ? "Unknown" : _info.Status;

    public string? ClassGuid => _info.ClassGuid;

    public string? Description => _info.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool? IsSigned => _info.IsSigned;

    public string SignedDisplay => IsSigned is null ? "Unknown" : (IsSigned.Value ? "Signed" : "Unsigned");

    public string? InfName => _info.InfName;

    public bool HasInfName => !string.IsNullOrWhiteSpace(InfName);

    public string? DeviceId => _info.DeviceId;

    public bool HasDeviceId => !string.IsNullOrWhiteSpace(DeviceId);

    public int? ProblemCode => _info.ProblemCode;

    public bool HasProblemCode => ProblemCode.HasValue;

    public string HardwareIdsDisplay => _info.HardwareIds.Count == 0
        ? "No hardware IDs"
        : string.Join(Environment.NewLine, _info.HardwareIds);

    public bool CanMaintain => !string.IsNullOrWhiteSpace(InfName);
}

public sealed class DriverHealthInsightViewModel
{
    private readonly DriverHealthInsight _insight;

    public DriverHealthInsightViewModel(DriverHealthInsight insight)
    {
        _insight = insight ?? throw new ArgumentNullException(nameof(insight));
    }

    public string DeviceName => string.IsNullOrWhiteSpace(_insight.DeviceName) ? "Unnamed device" : _insight.DeviceName;

    public string Issue => _insight.Issue;

    public string? Detail => _insight.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string? InfName => _insight.InfName;

    public bool HasInfName => !string.IsNullOrWhiteSpace(InfName);

    public DriverHealthSeverity Severity => _insight.Severity;
}

public enum DriverHealthSeverity
{
    Advisory,
    Warning,
    Critical
}

public sealed record DriverHealthInsight(string? DeviceName, string Issue, string? Detail, string? InfName, DriverHealthSeverity Severity);

public sealed class GpuVendorGuidanceViewModel
{
    private readonly GpuVendorGuidance _guidance;

    public GpuVendorGuidanceViewModel(GpuVendorGuidance guidance)
    {
        _guidance = guidance ?? throw new ArgumentNullException(nameof(guidance));
    }

    public string VendorLabel => _guidance.VendorLabel;

    public string Message => _guidance.Message;

    public string LinkLabel => _guidance.LinkLabel;

    public Uri SupportUri => _guidance.SupportUri;
}

public sealed record GpuVendorGuidance(string VendorKey, string VendorLabel, string Message, string LinkLabel, Uri SupportUri);

internal sealed record GpuVendorGuidanceDescriptor(string VendorLabel, string Message, string LinkLabel, Uri SupportUri);
