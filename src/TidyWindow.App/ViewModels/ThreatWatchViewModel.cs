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
using TidyWindow.Core.Processes.ThreatWatch;

namespace TidyWindow.App.ViewModels;

public sealed partial class ThreatWatchViewModel : ViewModelBase
{
    private readonly ThreatWatchScanService _scanService;
    private readonly ProcessStateStore _stateStore;
    private readonly IUserConfirmationService _confirmationService;
    private readonly MainViewModel _mainViewModel;
    private bool _isInitialized;

    public ThreatWatchViewModel(
        ThreatWatchScanService scanService,
        ProcessStateStore stateStore,
        IUserConfirmationService confirmationService,
        MainViewModel mainViewModel)
    {
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        SeverityGroups = new ObservableCollection<ThreatWatchSeverityGroupViewModel>(
            new[]
            {
                new ThreatWatchSeverityGroupViewModel(SuspicionLevel.Red, "Critical", "Immediate action recommended"),
                new ThreatWatchSeverityGroupViewModel(SuspicionLevel.Orange, "Elevated", "Review and confirm intent"),
                new ThreatWatchSeverityGroupViewModel(SuspicionLevel.Yellow, "Watch", "Likely safe but worth triage")
            });

        Hits = new ObservableCollection<ThreatWatchHitViewModel>();
        HitsView = CollectionViewSource.GetDefaultView(Hits);
        HitsView.Filter = FilterHit;
    }

    public ObservableCollection<ThreatWatchSeverityGroupViewModel> SeverityGroups { get; }

    public ObservableCollection<ThreatWatchHitViewModel> Hits { get; }

    public ICollectionView HitsView { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasHits;

    [ObservableProperty]
    private string _summary = "Preparing Threat Watch telemetry...";

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
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            IsBusy = true;
            _mainViewModel.SetStatusMessage("Scanning running processes...");
            var result = await _scanService.RunScanAsync();
            ApplyHits(result.Hits);
            Summary = BuildSummary(result);
            LastScanCompletedAt = result.CompletedAtUtc;
            OnPropertyChanged(nameof(LastScanSummary));
            LogScanOutcome(result);
        }
        catch (Exception ex)
        {
            Summary = "Threat Watch scan failed.";
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "Threat Watch", "Scan failed.", new[] { ex.Message });
        }
        finally
        {
            var minimumDelay = TimeSpan.FromMilliseconds(1200);
            var elapsed = stopwatch.Elapsed;
            if (elapsed < minimumDelay)
            {
                await Task.Delay(minimumDelay - elapsed);
            }

            IsBusy = false;
            _mainViewModel.SetStatusMessage("Ready");
        }
    }

    internal Task WhitelistAsync(ThreatWatchHitViewModel? hit)
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
                _stateStore.UpsertWhitelistEntry(ThreatWatchWhitelistEntry.CreateDirectory(directory, notes: $"Whitelisted {hit.ProcessName}"));
            }

            _stateStore.UpsertWhitelistEntry(ThreatWatchWhitelistEntry.CreateProcess(hit.ProcessName, notes: "Whitelisted via Threat Watch"));
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

    internal async Task IgnoreAsync(ThreatWatchHitViewModel? hit)
    {
        if (hit is null)
        {
            return;
        }

        await Task.Run(() => _stateStore.RemoveSuspiciousHit(hit.Id));
        RemoveHit(hit);
        hit.LastActionMessage = "Marked as resolved.";
    }

    internal async Task ScanFileAsync(ThreatWatchHitViewModel? hit)
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

    internal void OpenLocation(ThreatWatchHitViewModel? hit)
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

    internal async Task QuarantineAsync(ThreatWatchHitViewModel? hit)
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

        var intelResult = await TryScanFileAsync(hit.FilePath);
        var success = await Task.Run(() => TerminateProcessesByPath(hit.FilePath));

        var baseMessage = success
            ? "Process terminated. Re-run scan to confirm."
            : "No running processes matched that file.";
        hit.LastActionMessage = AppendIntelMessage(baseMessage, intelResult);

        if (!string.IsNullOrWhiteSpace(hit.FilePath))
        {
            try
            {
                var entry = ThreatWatchQuarantineEntry.Create(
                    hit.ProcessName,
                    hit.FilePath,
                    notes: success ? "Process terminated via Threat Watch" : "Marked for quarantine",
                    addedBy: Environment.UserName,
                    verdict: intelResult?.Verdict,
                    verdictSource: intelResult?.Source,
                    verdictDetails: intelResult?.Details,
                    sha256: intelResult?.Sha256);
                _stateStore.UpsertQuarantineEntry(entry);
                LogQuarantineEntry(hit, entry, intelResult);
            }
            catch
            {
                // Ignore persistence errors so the quarantine action result still surfaces to the user.
            }
        }
    }

    private async Task<ThreatIntelResult?> TryScanFileAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return await _scanService.ScanFileAsync(filePath);
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Warning, "Threat Watch", "Quarantine succeeded, but Defender scan failed.", new[] { ex.Message });
            return null;
        }
    }

    private static string AppendIntelMessage(string baseMessage, ThreatIntelResult? intelResult)
    {
        if (intelResult is null)
        {
            return baseMessage;
        }

        var intel = intelResult.Value;
        var verdictLabel = intel.Verdict switch
        {
            ThreatIntelVerdict.KnownBad => " Defender flagged the file as malicious.",
            ThreatIntelVerdict.KnownGood => " Defender marked the file as trusted.",
            _ => " Defender had no opinion."
        };

        if (!string.IsNullOrWhiteSpace(intel.Source))
        {
            verdictLabel = verdictLabel.TrimEnd('.') + $" ({intel.Source}).";
        }

        return baseMessage + verdictLabel;
    }

    private void LogQuarantineEntry(ThreatWatchHitViewModel hit, ThreatWatchQuarantineEntry entry, ThreatIntelResult? intelResult)
    {
        var details = new List<string>
        {
            entry.FilePath,
            entry.Notes ?? string.Empty
        };

        if (!string.IsNullOrWhiteSpace(entry.AddedBy))
        {
            details.Add($"Logged by {entry.AddedBy}");
        }

        if (intelResult is { } intel)
        {
            details.Add($"Verdict: {intel.Verdict} ({intel.Source ?? "Defender"})");
            if (!string.IsNullOrWhiteSpace(intel.Details))
            {
                details.Add(intel.Details);
            }

            if (!string.IsNullOrWhiteSpace(intel.Sha256))
            {
                details.Add($"SHA-256: {intel.Sha256}");
            }
        }

        _mainViewModel.LogActivityInformation("Threat Watch", $"Captured quarantine record for {hit.ProcessName}.", details.Where(static d => !string.IsNullOrWhiteSpace(d)));
    }

    private string BuildSummary(ThreatWatchDetectionResult result)
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

    private void LogScanOutcome(ThreatWatchDetectionResult result)
    {
        if (result.Hits.Count == 0)
        {
            _mainViewModel.LogActivityInformation("Threat Watch", "No suspicious activity detected.");
            return;
        }

        var detailLines = BuildHitDetails(result.Hits, 12);
        var message = result.Hits.Count == 1
            ? "Threat Watch flagged 1 suspicious process."
            : $"Threat Watch flagged {result.Hits.Count} suspicious processes.";
        _mainViewModel.LogActivity(ActivityLogLevel.Warning, "Threat Watch", message, detailLines);
    }

    private static IEnumerable<string> BuildHitDetails(IEnumerable<SuspiciousProcessHit> hits, int max)
    {
        var ordered = hits
            .OrderByDescending(hit => hit.Level)
            .ThenBy(hit => hit.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            return Array.Empty<string>();
        }

        var limit = Math.Min(max, ordered.Count);
        var details = new List<string>(limit + 1);
        for (var i = 0; i < limit; i++)
        {
            var hit = ordered[i];
            var location = string.IsNullOrWhiteSpace(hit.FilePath) ? "(unknown location)" : hit.FilePath;
            details.Add($"{hit.Level}: {hit.ProcessName} — {location}");
        }

        if (ordered.Count > limit)
        {
            details.Add($"(+{ordered.Count - limit} more)");
        }

        return details;
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

            var vm = new ThreatWatchHitViewModel(this, hit);
            targetGroup.Hits.Add(vm);
            Hits.Add(vm);
        }

        HasHits = SeverityGroups.Any(group => group.Hits.Count > 0);
        HitsView.Refresh();
    }

    private void RemoveHit(ThreatWatchHitViewModel hit)
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
        if (obj is not ThreatWatchHitViewModel hit)
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

public sealed partial class ThreatWatchSeverityGroupViewModel : ObservableObject
{
    public ThreatWatchSeverityGroupViewModel(SuspicionLevel level, string title, string description)
    {
        Level = level;
        Title = title;
        Description = description;
        Hits = new ObservableCollection<ThreatWatchHitViewModel>();
        _isExpanded = level != SuspicionLevel.Yellow;
    }

    public SuspicionLevel Level { get; }

    public string Title { get; }

    public string Description { get; }

    public ObservableCollection<ThreatWatchHitViewModel> Hits { get; }

    [ObservableProperty]
    private bool _isExpanded;
}

public sealed partial class ThreatWatchHitViewModel : ObservableObject
{
    private readonly ThreatWatchViewModel _owner;

    public ThreatWatchHitViewModel(ThreatWatchViewModel owner, SuspiciousProcessHit hit)
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

    public string Source => string.IsNullOrWhiteSpace(Hit.Source) ? "Threat Watch" : Hit.Source;

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
