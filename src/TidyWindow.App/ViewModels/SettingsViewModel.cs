using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using WindowsClipboard = System.Windows.Clipboard;

namespace TidyWindow.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _mainViewModel;
    private readonly UserPreferencesService _preferences;
    private readonly IUpdateService _updateService;
    private readonly IUpdateInstallerService _updateInstallerService;
    private readonly ITrayService _trayService;
    private readonly string _currentVersion;

    private bool _telemetryEnabled;
    private bool _runInBackground;
    private bool _launchAtStartup;
    private bool _notificationsEnabled;
    private bool _notifyOnlyWhenInactive;
    private bool _pulseGuardEnabled;
    private bool _pulseGuardShowSuccessSummaries;
    private bool _pulseGuardShowActionAlerts;
    private PrivilegeMode _currentPrivilegeMode;
    private bool _isApplyingPreferences;
    private UpdateCheckResult? _updateResult;
    private bool _isCheckingForUpdates;
    private bool _hasAttemptedUpdateCheck;
    private string _updateStatusMessage = "Updates have not been checked yet.";
    private static readonly TimeSpan MinimumCheckDuration = TimeSpan.FromMilliseconds(1200);
    private bool _isInstallingUpdate;
    private long _installerBytesReceived;
    private long? _installerTotalBytes;
    private static readonly HttpClient ReleaseNotesHttpClient = CreateReleaseNotesClient();
    private static readonly JsonSerializerOptions GitHubSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private bool _hasFetchedFullReleaseNotes;
    private bool _isReleaseNotesDialogVisible;
    private IReadOnlyList<ReleaseNoteLine> _releaseNotesDisplayLines = Array.Empty<ReleaseNoteLine>();
    private string _releaseNotesMarkdown = string.Empty;
    private FlowDocument? _releaseNotesDocument;

    public SettingsViewModel(
        MainViewModel mainViewModel,
        IPrivilegeService privilegeService,
        UserPreferencesService preferences,
        IUpdateService updateService,
        IUpdateInstallerService updateInstallerService,
        ITrayService trayService)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _updateInstallerService = updateInstallerService ?? throw new ArgumentNullException(nameof(updateInstallerService));
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _currentPrivilegeMode = privilegeService?.CurrentMode ?? PrivilegeMode.Administrator;
        _currentVersion = string.IsNullOrWhiteSpace(_updateService.CurrentVersion)
            ? "0.0.0"
            : _updateService.CurrentVersion;

        ApplyPreferences(_preferences.Current);
        WeakEventManager<UserPreferencesService, UserPreferencesChangedEventArgs>.AddHandler(_preferences, nameof(UserPreferencesService.PreferencesChanged), OnPreferencesChanged);

        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, CanCheckForUpdates);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, CanInstallUpdate);
        ShowReleaseNotesCommand = new RelayCommand(ShowReleaseNotes, CanShowReleaseNotes);
        CloseReleaseNotesCommand = new RelayCommand(CloseReleaseNotes);
        CopyReleaseNotesCommand = new RelayCommand(CopyReleaseNotes, () => HasReleaseNotesContent);
        OpenReleaseNotesLinkCommand = new RelayCommand(OpenReleaseNotesLink, () => LatestReleaseNotesUri is not null);
    }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }
    public IAsyncRelayCommand InstallUpdateCommand { get; }
    public IRelayCommand ShowReleaseNotesCommand { get; }
    public IRelayCommand CloseReleaseNotesCommand { get; }
    public IRelayCommand CopyReleaseNotesCommand { get; }
    public IRelayCommand OpenReleaseNotesLinkCommand { get; }

    public bool TelemetryEnabled
    {
        get => _telemetryEnabled;
        set
        {
            if (SetProperty(ref _telemetryEnabled, value))
            {
                PublishStatus(value
                    ? "Telemetry sharing is enabled."
                    : "Telemetry sharing is disabled.");
            }
        }
    }

    public bool IsRunningElevated => CurrentPrivilegeMode == PrivilegeMode.Administrator;

    public string CurrentPrivilegeDisplay => IsRunningElevated
        ? "Current session: Administrator mode"
        : "Current session: User mode";

    public string CurrentPrivilegeAdvice => "TidyWindow relaunches with administrative rights so installs, registry updates, and repairs can finish without interruptions.";

    public bool RunInBackground
    {
        get => _runInBackground;
        set
        {
            if (SetProperty(ref _runInBackground, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Background mode enabled. TidyWindow will minimize to the tray."
                    : "Background mode disabled. TidyWindow will close when you exit.");
                _preferences.SetRunInBackground(value);
            }
        }
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (SetProperty(ref _notificationsEnabled, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "PulseGuard notifications will resume."
                    : "PulseGuard notifications are paused.");
                _preferences.SetNotificationsEnabled(value);
                OnPropertyChanged(nameof(CanAdjustPulseGuardNotifications));
            }
        }
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (SetProperty(ref _launchAtStartup, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "TidyWindow will now register a Task Scheduler entry to launch at sign-in."
                    : "TidyWindow will no longer auto-launch at sign-in.");
                _preferences.SetLaunchAtStartup(value);
            }
        }
    }

    public bool NotifyOnlyWhenInactive
    {
        get => _notifyOnlyWhenInactive;
        set
        {
            if (SetProperty(ref _notifyOnlyWhenInactive, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Toasts will only appear when TidyWindow is not focused."
                    : "Toasts may appear even while you are using the app.");
                _preferences.SetNotifyOnlyWhenInactive(value);
            }
        }
    }

    public bool PulseGuardEnabled
    {
        get => _pulseGuardEnabled;
        set
        {
            if (SetProperty(ref _pulseGuardEnabled, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "PulseGuard is standing watch."
                    : "PulseGuard is taking a break.");
                _preferences.SetPulseGuardEnabled(value);
                OnPropertyChanged(nameof(CanAdjustPulseGuardNotifications));
            }
        }
    }

    public bool PulseGuardShowSuccessSummaries
    {
        get => _pulseGuardShowSuccessSummaries;
        set
        {
            if (SetProperty(ref _pulseGuardShowSuccessSummaries, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Completion digests will be surfaced."
                    : "Completion digests are muted.");
                _preferences.SetShowSuccessSummaries(value);
            }
        }
    }

    public bool PulseGuardShowActionAlerts
    {
        get => _pulseGuardShowActionAlerts;
        set
        {
            if (SetProperty(ref _pulseGuardShowActionAlerts, value))
            {
                if (_isApplyingPreferences)
                {
                    return;
                }

                PublishStatus(value
                    ? "Action-required alerts are enabled."
                    : "Action-required alerts are muted.");
                _preferences.SetShowActionAlerts(value);
            }
        }
    }

    public bool CanAdjustPulseGuardNotifications => NotificationsEnabled && PulseGuardEnabled;

    public string CurrentVersionDisplay => _currentVersion;

    public string LatestVersionDisplay => _updateResult?.LatestVersion ?? "Unknown";

    public bool IsUpdateAvailable => _updateResult?.IsUpdateAvailable ?? false;

    public string UpdateChannelDisplay => _updateResult?.Channel ?? "stable";

    public string LatestReleaseSummary => _updateResult is { Summary: { Length: > 0 } summary }
        ? summary
        : "Release summary will appear after the first check.";

    public string LatestReleasePublishedDisplay => FormatTimestamp(_updateResult?.PublishedAtUtc);

    public string InstallerSizeDisplay => FormatSize(_updateResult?.InstallerSizeBytes);

    public string LatestIntegrityDisplay => string.IsNullOrWhiteSpace(_updateResult?.Sha256)
        ? "Not provided"
        : _updateResult!.Sha256!;

    public Uri? LatestReleaseNotesUri => _updateResult?.ReleaseNotesUri;

    public Uri? LatestDownloadUri => _updateResult?.DownloadUri;

    public bool HasReleaseNotesLink => LatestReleaseNotesUri is not null;

    public bool HasDownloadLink => LatestDownloadUri is not null;

    public bool IsReleaseNotesDialogVisible
    {
        get => _isReleaseNotesDialogVisible;
        private set => SetProperty(ref _isReleaseNotesDialogVisible, value);
    }

    public IReadOnlyList<ReleaseNoteLine> ReleaseNotesDisplayLines => _releaseNotesDisplayLines;

    public FlowDocument? ReleaseNotesDocument => _releaseNotesDocument;

    public bool HasReleaseNotesContent => _releaseNotesDocument is not null;

    public bool HasReleaseNotes => HasReleaseNotesContent || HasReleaseNotesLink;

    public string ReleaseNotesDialogTitle => _updateResult is null
        ? "Release notes"
        : $"Release notes - {_updateResult.LatestVersion}";

    public string ReleaseNotesDialogSubtitle => _updateResult is { PublishedAtUtc: { } publishedAt }
        ? $"{FormatTimestamp(publishedAt)} - {UpdateChannelDisplay} channel"
        : "Release notes will show after the first check.";

    public bool HasAttemptedUpdateCheck
    {
        get => _hasAttemptedUpdateCheck;
        private set
        {
            if (SetProperty(ref _hasAttemptedUpdateCheck, value))
            {
                OnPropertyChanged(nameof(LastUpdateCheckDisplay));
            }
        }
    }

    public string LastUpdateCheckDisplay => _updateResult is null
        ? (HasAttemptedUpdateCheck ? "Latest attempt did not complete." : "Never checked")
        : FormatTimestamp(_updateResult.CheckedAtUtc);

    public string UpdateAvailabilitySummary
    {
        get
        {
            if (_updateResult is null)
            {
                return "Updates have not been checked yet.";
            }

            return _updateResult.IsUpdateAvailable
                ? $"Update available: {_updateResult.LatestVersion}"
                : $"You're running the latest release ({_updateResult.CurrentVersion}).";
        }
    }

    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set => SetProperty(ref _updateStatusMessage, value);
    }

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                OnPropertyChanged(nameof(CheckForUpdatesButtonLabel));
                OnPropertyChanged(nameof(IsUpdateActionsEnabled));
                CheckForUpdatesCommand.NotifyCanExecuteChanged();
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CheckForUpdatesButtonLabel => IsCheckingForUpdates ? "Checking..." : "Check now";

    public bool IsInstallingUpdate
    {
        get => _isInstallingUpdate;
        private set
        {
            if (SetProperty(ref _isInstallingUpdate, value))
            {
                OnPropertyChanged(nameof(InstallUpdateButtonLabel));
                OnPropertyChanged(nameof(IsInstallUpdateVisible));
                OnPropertyChanged(nameof(ShowInstallerProgress));
                OnPropertyChanged(nameof(IsUpdateActionsEnabled));
                CheckForUpdatesCommand.NotifyCanExecuteChanged();
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string InstallUpdateButtonLabel => IsInstallingUpdate ? "Applying..." : "Install update";

    public bool IsInstallUpdateVisible => IsUpdateAvailable || IsInstallingUpdate;

    public bool ShowInstallerProgress => IsInstallingUpdate;

    public bool IsInstallerProgressIndeterminate => !(_installerTotalBytes.HasValue && _installerTotalBytes > 0);

    public double InstallerDownloadProgress =>
        _installerTotalBytes.HasValue && _installerTotalBytes > 0
            ? Math.Clamp((double)_installerBytesReceived / _installerTotalBytes.Value * 100d, 0d, 100d)
            : 0d;

    public string InstallerDownloadStatus
    {
        get
        {
            if (_installerBytesReceived <= 0)
            {
                return "Waiting to start download...";
            }

            if (_installerTotalBytes.HasValue && _installerTotalBytes > 0)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} of {1}",
                    FormatSize(_installerBytesReceived),
                    FormatSize(_installerTotalBytes));
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                "{0} downloaded",
                FormatSize(_installerBytesReceived));
        }
    }

    public bool IsUpdateActionsEnabled => !IsCheckingForUpdates && !IsInstallingUpdate;

    private PrivilegeMode CurrentPrivilegeMode
    {
        get => _currentPrivilegeMode;
        set
        {
            if (SetProperty(ref _currentPrivilegeMode, value))
            {
                OnPropertyChanged(nameof(IsRunningElevated));
                OnPropertyChanged(nameof(CurrentPrivilegeDisplay));
                OnPropertyChanged(nameof(CurrentPrivilegeAdvice));
            }
        }
    }

    private void PublishStatus(string message)
    {
        _mainViewModel.SetStatusMessage(message);
    }

    private void ApplyPreferences(UserPreferences preferences)
    {
        _isApplyingPreferences = true;
        try
        {
            RunInBackground = preferences.RunInBackground;
            LaunchAtStartup = preferences.LaunchAtStartup;
            PulseGuardEnabled = preferences.PulseGuardEnabled;
            NotificationsEnabled = preferences.NotificationsEnabled;
            NotifyOnlyWhenInactive = preferences.NotifyOnlyWhenInactive;
            PulseGuardShowSuccessSummaries = preferences.PulseGuardShowSuccessSummaries;
            PulseGuardShowActionAlerts = preferences.PulseGuardShowActionAlerts;
            OnPropertyChanged(nameof(CanAdjustPulseGuardNotifications));
        }
        finally
        {
            _isApplyingPreferences = false;
        }
    }

    private void OnPreferencesChanged(object? sender, UserPreferencesChangedEventArgs args)
    {
        ApplyPreferences(args.Preferences);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        HasAttemptedUpdateCheck = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            IsCheckingForUpdates = true;
            UpdateStatusMessage = "Checking for updates...";

            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(true);
            ApplyUpdateResult(result);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "Update check was cancelled.";
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = "Unable to contact the update service. Please try again.";
            _mainViewModel.LogActivityInformation("Updates", $"Update check failed: {ex.Message}");
        }
        finally
        {
            var remaining = MinimumCheckDuration - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(true);
            }

            IsCheckingForUpdates = false;
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates && !IsInstallingUpdate;

    private bool CanInstallUpdate() => IsUpdateAvailable && !IsCheckingForUpdates && !IsInstallingUpdate;

    private async Task InstallUpdateAsync()
    {
        if (_updateResult is null || !_updateResult.IsUpdateAvailable)
        {
            UpdateStatusMessage = "No update is available to install.";
            return;
        }

        try
        {
            IsInstallingUpdate = true;
            UpdateStatusMessage = "Downloading the update to a temp folder; you'll be asked before it runs.";
            ResetInstallerProgress();

            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                var total = p.TotalBytes ?? _updateResult.InstallerSizeBytes;
                _installerBytesReceived = Math.Max(0, p.BytesReceived);
                _installerTotalBytes = total;
                RaiseInstallerProgressProperties();
            });

            var result = await _updateInstallerService.DownloadAndInstallAsync(_updateResult, progress).ConfigureAwait(true);

            if (result.Launched)
            {
                UpdateStatusMessage = "Installer launched. TidyWindow will close so the upgrade can finish.";
                _mainViewModel.LogActivityInformation(
                    "Updates",
                    $"Installer launched at {result.InstallerPath}. Hash verified: {result.HashVerified}.");

                await Task.Delay(1500).ConfigureAwait(true);
                ShutdownForInstaller();
            }
            else
            {
                UpdateStatusMessage = "Installer download completed. Launch was cancelled.";
                _mainViewModel.LogActivityInformation(
                    "Updates",
                    $"Installer download completed at {result.InstallerPath}, but launch was cancelled by the user. Hash verified: {result.HashVerified}.");
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "Update installation was cancelled.";
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = "Update installation failed. Please try again.";
            _mainViewModel.LogActivityInformation("Updates", $"Update installation failed: {ex.Message}");
        }
        finally
        {
            IsInstallingUpdate = false;
            ResetInstallerProgress();
        }
    }

    private void ShutdownForInstaller()
    {
        try
        {
            _trayService.PrepareForExit();
        }
        catch
        {
            // Non-fatal; continue shutdown
        }

        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        _updateResult = result ?? throw new ArgumentNullException(nameof(result));

        UpdateStatusMessage = result.IsUpdateAvailable
            ? $"TidyWindow {result.LatestVersion} is available."
            : $"You're already running the latest release ({result.CurrentVersion}).";

        PublishStatus(result.IsUpdateAvailable
            ? $"Update available: {result.LatestVersion}"
            : "No updates available.");

        _mainViewModel.LogActivityInformation(
            "Updates",
            result.IsUpdateAvailable
                ? $"Update available: {result.LatestVersion}"
                : $"No updates available (current {result.CurrentVersion}).",
            BuildUpdateDetails(result));

        RefreshReleaseNotesContent();
        RaiseUpdateProperties();
    }

    private void RaiseUpdateProperties()
    {
        OnPropertyChanged(nameof(LatestVersionDisplay));
        OnPropertyChanged(nameof(UpdateAvailabilitySummary));
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(IsInstallUpdateVisible));
        OnPropertyChanged(nameof(UpdateChannelDisplay));
        OnPropertyChanged(nameof(LatestReleaseSummary));
        OnPropertyChanged(nameof(LatestReleasePublishedDisplay));
        OnPropertyChanged(nameof(InstallerSizeDisplay));
        OnPropertyChanged(nameof(LatestIntegrityDisplay));
        OnPropertyChanged(nameof(LatestReleaseNotesUri));
        OnPropertyChanged(nameof(LatestDownloadUri));
        OnPropertyChanged(nameof(HasReleaseNotesLink));
        OnPropertyChanged(nameof(ReleaseNotesDisplayLines));
        OnPropertyChanged(nameof(HasReleaseNotesContent));
        OnPropertyChanged(nameof(HasReleaseNotes));
        OnPropertyChanged(nameof(ReleaseNotesDialogTitle));
        OnPropertyChanged(nameof(ReleaseNotesDialogSubtitle));
        OnPropertyChanged(nameof(HasDownloadLink));
        OnPropertyChanged(nameof(LastUpdateCheckDisplay));
        InstallUpdateCommand.NotifyCanExecuteChanged();
        ShowReleaseNotesCommand.NotifyCanExecuteChanged();
        CopyReleaseNotesCommand.NotifyCanExecuteChanged();
        OpenReleaseNotesLinkCommand.NotifyCanExecuteChanged();
    }

    private void RefreshReleaseNotesContent()
    {
        SetReleaseNotesMarkdown(_updateResult?.Summary);
        _hasFetchedFullReleaseNotes = false;
        _ = TryFetchFullReleaseNotesAsync();

        if (!HasReleaseNotes)
        {
            IsReleaseNotesDialogVisible = false;
        }

        UpdateReleaseNotesCommands();
    }

    private async Task<bool> TryFetchFullReleaseNotesAsync()
    {
        var uri = LatestReleaseNotesUri;
        if (uri is null || _updateResult is null)
        {
            return false;
        }

        try
        {
            var apiUri = new Uri($"https://api.github.com/repos/Cosmos-0118/TidyWindow/releases/tags/v{_updateResult.LatestVersion}");
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
            using var response = await ReleaseNotesHttpClient.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream, GitHubSerializerOptions).ConfigureAwait(false);
            if (payload?.Body is null || payload.Body.Length == 0)
            {
                return false;
            }

            var parsed = BuildReleaseNotesLines(payload.Body);
            if (parsed.Count == 0)
            {
                return false;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SetReleaseNotesMarkdown(payload.Body);
                UpdateReleaseNotesCommands();
            });
            _hasFetchedFullReleaseNotes = true;
            return true;
        }
        catch
        {
            // Ignore fetch failures; fall back to manifest summary.
            return false;
        }
    }

    private void UpdateReleaseNotesCommands()
    {
        ShowReleaseNotesCommand.NotifyCanExecuteChanged();
        CopyReleaseNotesCommand.NotifyCanExecuteChanged();
        OpenReleaseNotesLinkCommand.NotifyCanExecuteChanged();
    }

    private void SetReleaseNotesMarkdown(string? markdown)
    {
        _releaseNotesMarkdown = NormalizeMarkdown(markdown);
        _releaseNotesDisplayLines = BuildReleaseNotesLines(_releaseNotesMarkdown);
        _releaseNotesDocument = BuildReleaseNotesDocument(_releaseNotesMarkdown);

        OnPropertyChanged(nameof(ReleaseNotesDisplayLines));
        OnPropertyChanged(nameof(ReleaseNotesDocument));
        OnPropertyChanged(nameof(HasReleaseNotesContent));
        OnPropertyChanged(nameof(HasReleaseNotes));
        OnPropertyChanged(nameof(ReleaseNotesDialogTitle));
        OnPropertyChanged(nameof(ReleaseNotesDialogSubtitle));
    }

    private async Task EnsureFullReleaseNotesAsync()
    {
        if (_hasFetchedFullReleaseNotes)
        {
            return;
        }

        if (!NeedsFullReleaseNotesFetch())
        {
            _hasFetchedFullReleaseNotes = true;
            return;
        }

        await TryFetchFullReleaseNotesAsync().ConfigureAwait(true);
    }

    private bool NeedsFullReleaseNotesFetch()
    {
        if (string.IsNullOrWhiteSpace(_releaseNotesMarkdown))
        {
            return true;
        }

        return _releaseNotesMarkdown.Contains("...", StringComparison.Ordinal);
    }

    private bool CanShowReleaseNotes() => HasReleaseNotes;

    private async void ShowReleaseNotes()
    {
        if (!HasReleaseNotes)
        {
            PublishStatus("Release notes are not available yet.");
            return;
        }

        await EnsureFullReleaseNotesAsync().ConfigureAwait(true);
        IsReleaseNotesDialogVisible = true;
    }

    private void CloseReleaseNotes()
    {
        IsReleaseNotesDialogVisible = false;
    }

    private void CopyReleaseNotes()
    {
        if (!HasReleaseNotesContent)
        {
            PublishStatus("No release notes to copy yet.");
            return;
        }

        try
        {
            var text = string.IsNullOrWhiteSpace(_releaseNotesMarkdown)
                ? string.Join(Environment.NewLine, _releaseNotesDisplayLines.Select(line => $"{line.Icon} {line.Text}"))
                : _releaseNotesMarkdown;
            WindowsClipboard.SetText(text);
            PublishStatus("Release notes copied to the clipboard.");
        }
        catch
        {
            PublishStatus("Unable to access the clipboard.");
        }
    }

    private void OpenReleaseNotesLink()
    {
        var uri = LatestReleaseNotesUri;
        if (uri is null)
        {
            PublishStatus("Release notes link is not available yet.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PublishStatus("Could not open the release notes link.");
            _mainViewModel.LogActivityInformation("Updates", $"Failed to open release notes link: {ex.Message}");
        }
    }

    private static IReadOnlyList<ReleaseNoteLine> BuildReleaseNotesLines(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Array.Empty<ReleaseNoteLine>();
        }

        var normalized = NormalizeLineEndings(NormalizeMarkdown(summary));
        var segments = Regex.Split(normalized, @"(?:\r?\n|\s+-\s*|^\s*-\s*)")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

        if (segments.Count == 0)
        {
            return Array.Empty<ReleaseNoteLine>();
        }

        var lines = new List<ReleaseNoteLine>(segments.Count);
        foreach (var segment in segments)
        {
            var text = NormalizeReleaseNoteText(segment);
            lines.Add(new ReleaseNoteLine(ResolveReleaseNoteIcon(text), text));
        }

        return lines;
    }

    private static string NormalizeReleaseNoteText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        return char.IsLower(trimmed[0]) ? char.ToUpper(trimmed[0], CultureInfo.CurrentCulture) + trimmed[1..] : trimmed;
    }

    private static string ResolveReleaseNoteIcon(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "•";
        }

        if (text.Contains("fix", StringComparison.OrdinalIgnoreCase))
        {
            return "🛠️";
        }

        if (text.Contains("add", StringComparison.OrdinalIgnoreCase) || text.Contains("new", StringComparison.OrdinalIgnoreCase))
        {
            return "✨";
        }

        if (text.Contains("improve", StringComparison.OrdinalIgnoreCase) || text.Contains("better", StringComparison.OrdinalIgnoreCase))
        {
            return "🚀";
        }

        if (text.Contains("security", StringComparison.OrdinalIgnoreCase) || text.Contains("defender", StringComparison.OrdinalIgnoreCase))
        {
            return "🛡️";
        }

        return "•";
    }

    private static FlowDocument? BuildReleaseNotesDocument(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240))
        };

        System.Windows.Documents.List? activeList = null;
        var isActiveListOrdered = false;
        var normalized = NormalizeLineEndings(NormalizeMarkdown(markdown));
        var lines = normalized.Split('\n');

        void FlushList()
        {
            if (activeList is not null)
            {
                document.Blocks.Add(activeList);
                activeList = null;
            }
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushList();
                continue;
            }

            if (TryParseHeading(line, out var level, out var headingText))
            {
                FlushList();
                document.Blocks.Add(CreateHeadingBlock(headingText, level));
                continue;
            }

            if (TryParseListItem(line, out var isOrdered, out var listText))
            {
                if (activeList is null || isOrdered != isActiveListOrdered)
                {
                    FlushList();
                    activeList = CreateList(isOrdered);
                    isActiveListOrdered = isOrdered;
                }

                activeList.ListItems.Add(new ListItem(CreateParagraphWithIcon(listText, includeIcon: true)));
                continue;
            }

            FlushList();
            document.Blocks.Add(CreateParagraphWithIcon(line, includeIcon: false));
        }

        FlushList();
        return document;
    }

    private static System.Windows.Documents.List CreateList(bool isOrdered) => new()
    {
        MarkerStyle = TextMarkerStyle.None,
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static Paragraph CreateHeadingBlock(string text, int level)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, level == 1 ? 0 : 6, 0, 4)
        };

        paragraph.Inlines.Add(new Run(text)
        {
            FontSize = level switch
            {
                1 => 18,
                2 => 16,
                3 => 15,
                _ => 14
            },
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252))
        });

        return paragraph;
    }

    private static Paragraph CreateParagraphWithIcon(string text, bool includeIcon)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8)
        };

        if (includeIcon)
        {
            paragraph.Inlines.Add(new Run($"{ResolveReleaseNoteIcon(text)} ")
            {
                Foreground = ReleaseNoteIconBrush,
                FontWeight = System.Windows.FontWeights.SemiBold
            });
        }

        AppendMarkdownInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static void AppendMarkdownInlines(InlineCollection inlines, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        const string inlinePattern = @"(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*|\[([^\]]+)\]\(([^)]+)\))";
        var matches = Regex.Matches(text, inlinePattern);
        var index = 0;

        foreach (Match match in matches)
        {
            if (match.Index > index)
            {
                inlines.Add(new Run(text.Substring(index, match.Index - index)));
            }

            var value = match.Value;
            if (value.StartsWith("`", StringComparison.Ordinal))
            {
                var code = value.Trim('`');
                var codeSpan = new Span(new Run(code))
                {
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono"),
                    Background = InlineCodeBackgroundBrush,
                    Foreground = InlineCodeForegroundBrush
                };
                inlines.Add(codeSpan);
            }
            else if (value.StartsWith("**", StringComparison.Ordinal))
            {
                inlines.Add(new Run(value[2..^2]) { FontWeight = System.Windows.FontWeights.SemiBold });
            }
            else if (value.StartsWith("*", StringComparison.Ordinal))
            {
                inlines.Add(new Run(value[1..^1]) { FontStyle = System.Windows.FontStyles.Italic });
            }
            else if (value.StartsWith("[", StringComparison.Ordinal) && match.Groups.Count >= 3)
            {
                var linkText = match.Groups[2].Value;
                var linkTarget = match.Groups[3].Value;
                Uri? uri = null;

                if (!Uri.TryCreate(linkTarget, UriKind.Absolute, out uri))
                {
                    Uri.TryCreate($"https://{linkTarget}", UriKind.Absolute, out uri);
                }

                if (uri is null)
                {
                    inlines.Add(new Run(linkText));
                }
                else
                {
                    var hyperlink = new Hyperlink(new Run(linkText))
                    {
                        NavigateUri = uri,
                        Foreground = ReleaseNoteLinkBrush
                    };

                    hyperlink.RequestNavigate += OnHyperlinkNavigate;
                    inlines.Add(hyperlink);
                }
            }

            index = match.Index + match.Length;
        }

        if (index < text.Length)
        {
            inlines.Add(new Run(text[index..]));
        }
    }

    private static bool TryParseHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        if (!line.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        var match = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
        if (!match.Success)
        {
            return false;
        }

        level = Math.Clamp(match.Groups[1].Value.Length, 1, 6);
        text = match.Groups[2].Value.Trim();
        return true;
    }

    private static bool TryParseListItem(string line, out bool isOrdered, out string text)
    {
        var unorderedMatch = Regex.Match(line, @"^[-*+]\s+(.*)$");
        if (unorderedMatch.Success)
        {
            isOrdered = false;
            text = unorderedMatch.Groups[1].Value.Trim();
            return true;
        }

        var orderedMatch = Regex.Match(line, @"^\d+\.\s+(.*)$");
        if (orderedMatch.Success)
        {
            isOrdered = true;
            text = orderedMatch.Groups[1].Value.Trim();
            return true;
        }

        isOrdered = false;
        text = string.Empty;
        return false;
    }

    private static void OnHyperlinkNavigate(object? sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // If navigation fails, keep the dialog open so the user can copy the link manually.
        }

        e.Handled = true;
    }

    private static string NormalizeMarkdown(string? markdown)
    {
        return string.IsNullOrWhiteSpace(markdown)
            ? string.Empty
            : markdown.Trim();
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    private static readonly System.Windows.Media.Brush ReleaseNoteIconBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
    private static readonly System.Windows.Media.Brush ReleaseNoteLinkBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 165, 250));
    private static readonly System.Windows.Media.Brush InlineCodeBackgroundBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59));
    private static readonly System.Windows.Media.Brush InlineCodeForegroundBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));

    public sealed record ReleaseNoteLine(string Icon, string Text);

    private sealed record GitHubReleaseResponse(string? Body);

    private static HttpClient CreateReleaseNotesClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWindow/ReleaseNotesFetcher");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static IEnumerable<string> BuildUpdateDetails(UpdateCheckResult result)
    {
        var details = new List<string>
        {
            $"Channel: {result.Channel}",
            $"Checked at (UTC): {result.CheckedAtUtc:u}"
        };

        if (result.PublishedAtUtc.HasValue)
        {
            details.Add($"Published: {result.PublishedAtUtc:yyyy-MM-dd HH:mm}Z");
        }

        if (result.ReleaseNotesUri is not null)
        {
            details.Add($"Release notes: {result.ReleaseNotesUri}");
        }

        if (result.DownloadUri is not null)
        {
            details.Add($"Installer: {result.DownloadUri}");
        }

        return details;
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "Unknown";
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is null || bytes <= 0)
        {
            return "Unknown";
        }

        const long OneMegabyte = 1024 * 1024;
        const long OneGigabyte = 1024L * 1024 * 1024;

        if (bytes >= OneGigabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:F1} GB", bytes.Value / (double)OneGigabyte);
        }

        if (bytes >= OneMegabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:F1} MB", bytes.Value / (double)OneMegabyte);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", bytes.Value / 1024d);
    }

    private void ResetInstallerProgress()
    {
        _installerBytesReceived = 0;
        _installerTotalBytes = _updateResult?.InstallerSizeBytes;
        RaiseInstallerProgressProperties();
    }

    private void RaiseInstallerProgressProperties()
    {
        OnPropertyChanged(nameof(InstallerDownloadProgress));
        OnPropertyChanged(nameof(IsInstallerProgressIndeterminate));
        OnPropertyChanged(nameof(InstallerDownloadStatus));
        OnPropertyChanged(nameof(ShowInstallerProgress));
    }
}
