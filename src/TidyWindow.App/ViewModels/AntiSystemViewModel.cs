using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Processes;
using TidyWindow.Core.Processes.AntiSystem;

namespace TidyWindow.App.ViewModels;

public sealed partial class AntiSystemViewModel : ViewModelBase
{
    private readonly AntiSystemScanService _scanService;
    private readonly ProcessStateStore _stateStore;
    private readonly IUserConfirmationService _confirmationService;
    private readonly MainViewModel _mainViewModel;
    private bool _isInitialized;

    public AntiSystemViewModel(
        AntiSystemScanService scanService,
        ProcessStateStore stateStore,
        IUserConfirmationService confirmationService,
        MainViewModel mainViewModel)
    {
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        SeverityGroups = new ObservableCollection<AntiSystemSeverityGroupViewModel>(
            new[]
            {
                new AntiSystemSeverityGroupViewModel(SuspicionLevel.Red, "Critical", "Immediate action recommended"),
                new AntiSystemSeverityGroupViewModel(SuspicionLevel.Orange, "Elevated", "Review and confirm intent"),
                new AntiSystemSeverityGroupViewModel(SuspicionLevel.Yellow, "Watch", "Likely safe but worth triage")
            });

        Hits = new ObservableCollection<AntiSystemHitViewModel>();
        HitsView = CollectionViewSource.GetDefaultView(Hits);
        HitsView.Filter = FilterHit;
    }

    public ObservableCollection<AntiSystemSeverityGroupViewModel> SeverityGroups { get; }

    public ObservableCollection<AntiSystemHitViewModel> Hits { get; }

    public ICollectionView HitsView { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasHits;

    [ObservableProperty]
    private string _summary = "Preparing Anti-System telemetry...";

    [ObservableProperty]
    private DateTimeOffset? _lastScanCompletedAt;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private SuspicionLevel? _filterLevel;

    public string? LastScanSummary => LastScanCompletedAt is null
        ? "Scan has not been run this session."
        : $"Last scan: {LastScanCompletedAt.Value.ToLocalTime():g}";

    public void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        var hits = _stateStore.GetSuspiciousHits();
        ApplyHits(hits);
        Summary = hits.Count == 0
            ? "No suspicious processes flagged yet."
            : $"{hits.Count} suspicious processes awaiting review.";
        OnPropertyChanged(nameof(LastScanSummary));
        _isInitialized = true;
    }

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
            _mainViewModel.SetStatusMessage("Scanning running processes...");
            var result = await _scanService.RunScanAsync();
            ApplyHits(result.Hits);
            Summary = BuildSummary(result);
            LastScanCompletedAt = result.CompletedAtUtc;
            OnPropertyChanged(nameof(LastScanSummary));
            _mainViewModel.LogActivityInformation("Anti-System", Summary);
        }
        catch (Exception ex)
        {
            Summary = "Anti-System scan failed.";
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Anti-System", "Scan failed.", new[] { ex.Message });
        }
        finally
        {
            IsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    internal Task WhitelistAsync(AntiSystemHitViewModel? hit)
    {
        if (hit is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            var directory = Path.GetDirectoryName(hit.FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                _stateStore.UpsertWhitelistEntry(AntiSystemWhitelistEntry.CreateDirectory(directory, notes: $"Whitelisted {hit.ProcessName}"));
            }

            _stateStore.UpsertWhitelistEntry(AntiSystemWhitelistEntry.CreateProcess(hit.ProcessName, notes: "Whitelisted via Anti-System"));
            _stateStore.RemoveSuspiciousHit(hit.Id);
            RemoveHit(hit);
            hit.LastActionMessage = "Whitelisted. Future scans will ignore this entry.";
        }
        catch (Exception ex)
        {
            hit.LastActionMessage = ex.Message;
        }

        return Task.CompletedTask;
    }

    internal async Task IgnoreAsync(AntiSystemHitViewModel? hit)
    {
        if (hit is null)
        {
            return;
        }

        await Task.Run(() => _stateStore.RemoveSuspiciousHit(hit.Id));
        RemoveHit(hit);
        hit.LastActionMessage = "Marked as resolved.";
    }

    internal async Task ScanFileAsync(AntiSystemHitViewModel? hit)
    {
        if (hit is null)
        {
            return;
        }

        if (!File.Exists(hit.FilePath))
        {
            hit.LastActionMessage = "File not found on disk.";
            return;
        }

        try
        {
            hit.IsBusy = true;
            var verdict = await _scanService.ScanFileAsync(hit.FilePath);
            var message = verdict.Verdict switch
            {
                ThreatIntelVerdict.KnownBad => $"Defender flagged the file ({verdict.Source}).",
                ThreatIntelVerdict.KnownGood => "File is trusted by Defender/blocklist.",
                _ => "No additional telemetry available."
            };
            hit.LastActionMessage = message;
        }
        catch (Exception ex)
        {
            hit.LastActionMessage = ex.Message;
        }
        finally
        {
            hit.IsBusy = false;
        }
    }

    internal void OpenLocation(AntiSystemHitViewModel? hit)
    {
        if (hit is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(hit.FilePath) || !File.Exists(hit.FilePath))
        {
            hit.LastActionMessage = "File not found.";
            return;
        }

        try
        {
            var argument = $"/select,\"{hit.FilePath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
            hit.LastActionMessage = "Opened file location.";
        }
        catch (Exception ex)
        {
            hit.LastActionMessage = ex.Message;
        }
    }

    internal async Task QuarantineAsync(AntiSystemHitViewModel? hit)
    {
        if (hit is null)
        {
            return;
        }

        if (!_confirmationService.Confirm("Quarantine process", $"Attempt to terminate any running instances of {hit.ProcessName}?"))
        {
            hit.LastActionMessage = "Quarantine cancelled.";
            return;
        }

        var success = await Task.Run(() => TerminateProcessesByPath(hit.FilePath));
        hit.LastActionMessage = success
            ? "Process terminated. Re-run scan to confirm."
            : "No running processes matched that file.";

        if (!string.IsNullOrWhiteSpace(hit.FilePath))
        {
            try
            {
                var entry = AntiSystemQuarantineEntry.Create(
                    hit.ProcessName,
                    hit.FilePath,
                    notes: success ? "Process terminated via Anti-System" : "Marked for quarantine",
                    addedBy: Environment.UserName);
                _stateStore.UpsertQuarantineEntry(entry);
            }
            catch
            {
                // Ignore persistence errors so the quarantine action result still surfaces to the user.
            }
        }
    }

    private string BuildSummary(AntiSystemDetectionResult result)
    {
        if (result.Hits.Count == 0)
        {
            HasHits = false;
            return "No suspicious activity detected.";
        }

        HasHits = true;
        var critical = result.Hits.Count(hit => hit.Level == SuspicionLevel.Red);
        var elevated = result.Hits.Count(hit => hit.Level == SuspicionLevel.Orange);
        var watch = result.Hits.Count(hit => hit.Level == SuspicionLevel.Yellow);
        return $"Critical: {critical} · Elevated: {elevated} · Watch: {watch}";
    }

    private void ApplyHits(IReadOnlyCollection<SuspiciousProcessHit> hits)
    {
        foreach (var group in SeverityGroups)
        {
            group.Hits.Clear();
        }

        Hits.Clear();

        foreach (var hit in hits.OrderByDescending(static h => h.ObservedAtUtc))
        {
            var targetGroup = SeverityGroups.FirstOrDefault(group => group.Level == hit.Level);
            if (targetGroup is null)
            {
                continue;
            }

            var vm = new AntiSystemHitViewModel(this, hit);
            targetGroup.Hits.Add(vm);
            Hits.Add(vm);
        }

        HasHits = SeverityGroups.Any(group => group.Hits.Count > 0);
        HitsView.Refresh();
    }

    private void RemoveHit(AntiSystemHitViewModel hit)
    {
        var group = SeverityGroups.FirstOrDefault(g => g.Level == hit.Level);
        if (group is null)
        {
            return;
        }

        group.Hits.Remove(hit);
        Hits.Remove(hit);
        HasHits = SeverityGroups.Any(g => g.Hits.Count > 0);
        HitsView.Refresh();
    }

    private static bool TerminateProcessesByPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalized = filePath.Trim();
        var terminated = false;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var candidatePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                if (string.Equals(candidatePath, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(true);
                    terminated = true;
                }
            }
            catch
            {
                // Ignore access issues for system processes.
            }
            finally
            {
                process.Dispose();
            }
        }

        return terminated;
    }

    partial void OnFilterTextChanged(string value) => HitsView.Refresh();

    partial void OnFilterLevelChanged(SuspicionLevel? value) => HitsView.Refresh();

    private bool FilterHit(object? obj)
    {
        if (obj is not AntiSystemHitViewModel hit)
        {
            return false;
        }

        if (FilterLevel is not null && hit.Level != FilterLevel)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        var query = FilterText.Trim();
        return hit.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || hit.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (hit.MatchedRules is { Count: > 0 } && hit.MatchedRules.Any(rule => rule.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterText = string.Empty;
        FilterLevel = null;
    }
}

public sealed partial class AntiSystemSeverityGroupViewModel : ObservableObject
{
    public AntiSystemSeverityGroupViewModel(SuspicionLevel level, string title, string description)
    {
        Level = level;
        Title = title;
        Description = description;
        Hits = new ObservableCollection<AntiSystemHitViewModel>();
        _isExpanded = level != SuspicionLevel.Yellow;
    }

    public SuspicionLevel Level { get; }

    public string Title { get; }

    public string Description { get; }

    public ObservableCollection<AntiSystemHitViewModel> Hits { get; }

    [ObservableProperty]
    private bool _isExpanded;
}

public sealed partial class AntiSystemHitViewModel : ObservableObject
{
    private readonly AntiSystemViewModel _owner;

    public AntiSystemHitViewModel(AntiSystemViewModel owner, SuspiciousProcessHit hit)
    {
        _owner = owner;
        Hit = hit;
    }

    public SuspiciousProcessHit Hit { get; }

    public string Id => Hit.Id;

    public SuspicionLevel Level => Hit.Level;

    public string ProcessName => Hit.ProcessName;

    public string FilePath => Hit.FilePath;

    public string DirectoryPath => Path.GetDirectoryName(Hit.FilePath) ?? string.Empty;

    public string Source => string.IsNullOrWhiteSpace(Hit.Source) ? "Anti-System" : Hit.Source;

    public string ObservedAt => Hit.ObservedAtUtc.ToLocalTime().ToString("g");

    public string SeverityLabel => Level switch
    {
        SuspicionLevel.Red => "Critical",
        SuspicionLevel.Orange => "Elevated",
        SuspicionLevel.Yellow => "Watch",
        _ => "Info"
    };

    public string MatchedRulesLabel => Hit.MatchedRules is { Count: > 0 }
        ? string.Join(", ", Hit.MatchedRules)
        : "No rules available";

    public bool HasRules => Hit.MatchedRules is { Count: > 0 };

    public IReadOnlyList<string> MatchedRules => Hit.MatchedRules ?? Array.Empty<string>();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastActionMessage;

    [RelayCommand]
    private Task WhitelistAsync() => _owner.WhitelistAsync(this);

    [RelayCommand]
    private Task IgnoreAsync() => _owner.IgnoreAsync(this);

    [RelayCommand]
    private Task ScanFileAsync() => _owner.ScanFileAsync(this);

    [RelayCommand]
    private void OpenLocation() => _owner.OpenLocation(this);

    [RelayCommand]
    private Task QuarantineAsync() => _owner.QuarantineAsync(this);
}
