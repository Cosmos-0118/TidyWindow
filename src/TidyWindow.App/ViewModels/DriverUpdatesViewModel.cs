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

        Summary = "Scan Windows Update for driver version differences.";
    }

    public ObservableCollection<DriverUpdateItemViewModel> Updates { get; }

    public ObservableCollection<string> Warnings { get; }

    public ObservableCollection<InstalledDriverItemViewModel> InstalledDrivers { get; }

    public ObservableCollection<string> SkipDetails { get; }

    public bool HasResults => Updates.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasInstalledDrivers => InstalledDrivers.Count > 0;

    public bool HasSkipDetails => SkipDetails.Count > 0;

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

    public bool HasScanned { get; private set; }

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(ActiveFiltersSummary);

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
            InstalledDrivers.Clear();

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

    private void OnUpdatesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasResults));
    }

    private void OnWarningsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWarnings));
    }

    private void OnInstalledDriversCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasInstalledDrivers));
        OnPropertyChanged(nameof(InstalledDriversSummary));
    }

    private void OnSkipDetailsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSkipDetails));
    }

    partial void OnActiveFiltersSummaryChanged(string? value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
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

    public bool ShowOptionalBadge => IsOptional && Status == DriverUpdateStatus.UpdateAvailable;

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

    public string? Classification => _info.Classification;

    public bool HasClassification => !string.IsNullOrWhiteSpace(Classification);

    public string? Severity => _info.Severity;

    public bool HasSeverity => !string.IsNullOrWhiteSpace(Severity);

    public string? UpdateId => _info.UpdateId;

    public int? RevisionNumber => _info.RevisionNumber;

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

    public string? DeviceId => _info.DeviceId;

    public int? ProblemCode => _info.ProblemCode;

    public string HardwareIdsDisplay => _info.HardwareIds.Count == 0
        ? "No hardware IDs"
        : string.Join(Environment.NewLine, _info.HardwareIds);
}
