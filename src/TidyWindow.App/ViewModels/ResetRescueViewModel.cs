using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.IO.Compression;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Backup;

namespace TidyWindow.App.ViewModels;

public sealed partial class ResetRescueViewModel : ViewModelBase
{
    private readonly BackupService _backupService;
    private readonly RestoreService _restoreService;
    private readonly InventoryService _inventoryService;
    private readonly MainViewModel _mainViewModel;

    private CancellationTokenSource? _cts;

    private bool _isBusy;
    private string _destinationPath = string.Empty;
    private string _restoreArchivePath = string.Empty;
    private string _status = "Select destination and items to protect.";
    private string _validationSummary = string.Empty;
    private double _progressValue;
    private string _progressText = string.Empty;
    private string? _lastArchive;
    private bool _isAppPickerOpen;
    private int _selectedAppCount;
    private string _selectedAppsPreview = "No apps selected";
    private string _appSearch = string.Empty;
    private BackupConflictStrategy _restoreConflictStrategy = BackupConflictStrategy.Rename;
    private string _pathMappingHint = string.Empty;

    public ResetRescueViewModel(
        BackupService backupService,
        RestoreService restoreService,
        InventoryService inventoryService,
        MainViewModel mainViewModel)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        Profiles = new ObservableCollection<SelectableBackupProfile>();
        Apps = new ObservableCollection<SelectableBackupApp>();
        Folders = new ObservableCollection<SelectableBackupFolder>();
        ConflictStrategies = new[]
        {
            BackupConflictStrategy.Rename,
            BackupConflictStrategy.BackupExisting,
            BackupConflictStrategy.Overwrite,
            BackupConflictStrategy.Skip
        };
        PathMappings = new ObservableCollection<PathMapping>();
    }

    public ObservableCollection<SelectableBackupProfile> Profiles { get; }

    public ObservableCollection<SelectableBackupApp> Apps { get; }

    public ObservableCollection<SelectableBackupFolder> Folders { get; }

    public IReadOnlyList<BackupConflictStrategy> ConflictStrategies { get; }

    public ObservableCollection<PathMapping> PathMappings { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanStart => !IsBusy;

    public bool CanCancel => IsBusy && _cts is { IsCancellationRequested: false };

    public string DestinationPath
    {
        get => _destinationPath;
        set => SetProperty(ref _destinationPath, value ?? string.Empty);
    }

    public string RestoreArchivePath
    {
        get => _restoreArchivePath;
        set => SetProperty(ref _restoreArchivePath, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value ?? string.Empty);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        set => SetProperty(ref _validationSummary, value ?? string.Empty);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, Math.Clamp(value, 0, 1));
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value ?? string.Empty);
    }

    public string? LastArchive
    {
        get => _lastArchive;
        set => SetProperty(ref _lastArchive, value);
    }

    public BackupConflictStrategy RestoreConflictStrategy
    {
        get => _restoreConflictStrategy;
        set => SetProperty(ref _restoreConflictStrategy, value);
    }

    public string PathMappingHint
    {
        get => _pathMappingHint;
        set => SetProperty(ref _pathMappingHint, value ?? string.Empty);
    }

    public bool IsAppPickerOpen
    {
        get => _isAppPickerOpen;
        set => SetProperty(ref _isAppPickerOpen, value);
    }

    public int SelectedAppCount
    {
        get => _selectedAppCount;
        set => SetProperty(ref _selectedAppCount, value);
    }

    public string SelectedAppsPreview
    {
        get => _selectedAppsPreview;
        set => SetProperty(ref _selectedAppsPreview, value ?? string.Empty);
    }

    public string AppSearch
    {
        get => _appSearch;
        set
        {
            if (SetProperty(ref _appSearch, value ?? string.Empty))
            {
                ApplyAppFilter();
            }
        }
    }

    [RelayCommand]
    private async Task RefreshInventoryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "Discovering users and apps…";
        ValidationSummary = string.Empty;

        try
        {
            var profileTask = _inventoryService.DiscoverProfilesAsync();
            var appsTask = _inventoryService.DiscoverAppsAsync();
            await Task.WhenAll(profileTask, appsTask);

            Profiles.Clear();
            foreach (var profile in profileTask.Result)
            {
                Profiles.Add(new SelectableBackupProfile(profile));
            }

            foreach (var existing in Apps)
            {
                existing.PropertyChanged -= OnAppPropertyChanged;
            }
            Apps.Clear();
            foreach (var app in appsTask.Result)
            {
                var selectable = new SelectableBackupApp(app);
                selectable.PropertyChanged += OnAppPropertyChanged;
                Apps.Add(selectable);
            }

            Folders.Clear();
            foreach (var profile in Profiles)
            {
                foreach (var path in profile.Paths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    Folders.Add(new SelectableBackupFolder(profile.Display, path));
                }
            }

            UpdateAppSelectionSummary();

            Status = "Inventory loaded. Select what to protect.";
            _mainViewModel.SetStatusMessage("Reset Rescue inventory refreshed.");
            _mainViewModel.LogActivityInformation("ResetRescue", "Inventory refreshed", new[] { $"Profiles={Profiles.Count}", $"Apps={Apps.Count}" });
        }
        catch (Exception ex)
        {
            Status = "Inventory failed.";
            ValidationSummary = ex.Message;
            _mainViewModel.SetStatusMessage("Inventory failed.");
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "ResetRescue", "Inventory failed", new[] { ex.Message });
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var resolvedDestination = ResolveDestinationArchivePath();

        var validation = ValidateBackup(resolvedDestination);
        ValidationSummary = validation;
        if (!string.IsNullOrWhiteSpace(validation))
        {
            return;
        }

        var sources = BuildSources();
        if (sources.Count == 0)
        {
            ValidationSummary = "Select at least one folder or app.";
            return;
        }

        var request = new BackupRequest
        {
            DestinationArchivePath = resolvedDestination,
            SourcePaths = sources,
            Generator = "TidyWindow ResetRescue"
        };

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "Preparing backup…";
        Status = "Running backup…";

        try
        {
            var progress = new Progress<BackupProgress>(update =>
            {
                var fraction = update.TotalEntries == 0 ? 0 : update.ProcessedEntries / (double)update.TotalEntries;
                ProgressValue = fraction;
                ProgressText = update.CurrentPath ?? string.Empty;
            });

            _mainViewModel.LogActivityInformation("ResetRescue", "Backup started", new[] { $"Dest={DestinationPath}", $"Sources={sources.Count}" });

            var result = await _backupService.CreateAsync(request, progress, _cts.Token);
            LastArchive = result.ArchivePath;
            Status = $"Backup complete: {result.TotalEntries} items";
            _mainViewModel.SetStatusMessage("Backup completed.");
            _mainViewModel.LogActivity(ActivityLogLevel.Success, "ResetRescue", "Backup complete", new[] { $"Entries={result.TotalEntries}", $"Bytes={result.TotalBytes}" });
        }
        catch (OperationCanceledException)
        {
            Status = "Backup canceled.";
        }
        catch (Exception ex)
        {
            Status = "Backup failed.";
            ValidationSummary = ex.Message;
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "ResetRescue", "Backup failed", new[] { ex.Message });
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task StartRestoreAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RestoreArchivePath) || !File.Exists(RestoreArchivePath))
        {
            ValidationSummary = "Provide a valid archive path to restore.";
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "Preparing restore…";
        Status = "Running restore…";

        try
        {
            var destinationRoot = string.IsNullOrWhiteSpace(DestinationPath)
                ? null
                : Path.HasExtension(DestinationPath)
                    ? Path.GetDirectoryName(DestinationPath)
                    : DestinationPath;

            var request = new RestoreRequest
            {
                ArchivePath = RestoreArchivePath,
                DestinationRoot = destinationRoot,
                ConflictStrategy = RestoreConflictStrategy,
                VerifyHashes = true,
                PathRemapping = BuildPathRemapping()
            };

            var progress = new Progress<RestoreProgress>(update =>
            {
                var fraction = update.TotalEntries == 0 ? 0 : update.ProcessedEntries / (double)update.TotalEntries;
                ProgressValue = fraction;
                ProgressText = update.CurrentPath ?? string.Empty;
            });

            _mainViewModel.LogActivityInformation("ResetRescue", "Restore started", new[] { $"Archive={RestoreArchivePath}" });

            var result = await _restoreService.RestoreAsync(request, progress, _cts.Token);
            Status = result.Issues.Count == 0 ? "Restore complete." : $"Restore finished with {result.Issues.Count} issue(s).";
            if (result.Issues.Count > 0)
            {
                ValidationSummary = string.Join(Environment.NewLine, result.Issues.Select(i => $"{i.Path}: {i.Message}"));
            }
            _mainViewModel.SetStatusMessage("Restore completed.");
            var level = result.Issues.Count == 0 ? ActivityLogLevel.Success : ActivityLogLevel.Warning;
            var details = new List<string>
            {
                $"Strategy={RestoreConflictStrategy}",
                $"Issues={result.Issues.Count}",
                $"Renamed={result.RenamedCount}",
                $"BackedUp={result.BackupCount}",
                $"Overwritten={result.OverwrittenCount}",
                $"Skipped={result.SkippedCount}"
            };
            if (result.Issues.Count > 0 && !string.IsNullOrWhiteSpace(ValidationSummary))
            {
                details.Add(ValidationSummary);
            }
            _mainViewModel.LogActivity(level, "ResetRescue", "Restore finished", details.ToArray());
        }
        catch (OperationCanceledException)
        {
            Status = "Restore canceled.";
        }
        catch (Exception ex)
        {
            Status = "Restore failed.";
            ValidationSummary = ex.Message;
            _mainViewModel.LogActivity(ActivityLogLevel.Error, "ResetRescue", "Restore failed", new[] { ex.Message });
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenAppPicker()
    {
        IsAppPickerOpen = true;
    }

    [RelayCommand]
    private void CloseAppPicker()
    {
        IsAppPickerOpen = false;
    }

    [RelayCommand]
    private void AddPathMapping()
    {
        PathMappings.Add(new PathMapping());
    }

    [RelayCommand]
    private void RemovePathMapping(PathMapping mapping)
    {
        if (mapping != null)
        {
            PathMappings.Remove(mapping);
        }
    }

    [RelayCommand]
    private async Task AutoMapPathsAsync()
    {
        if (!File.Exists(RestoreArchivePath))
        {
            PathMappingHint = "Select an archive to auto-map";
            return;
        }

        var manifest = await LoadManifestAsync(RestoreArchivePath);
        if (manifest == null)
        {
            PathMappingHint = "Could not read manifest";
            return;
        }

        var discoveredMappings = SuggestMappings(manifest);
        if (discoveredMappings.Count == 0)
        {
            PathMappingHint = "No obvious remaps found";
            return;
        }

        foreach (var mapping in discoveredMappings)
        {
            UpsertMapping(mapping.From, mapping.To);
        }

        PathMappingHint = "Suggested mappings applied";
    }

    private string ResolveDestinationArchivePath()
    {
        var path = DestinationPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TidyWindow", "ResetRescue");
            Directory.CreateDirectory(baseDir);
            path = Path.Combine(baseDir, $"reset-rescue-{DateTime.Now:yyyyMMdd-HHmmss}.rrarchive");
            DestinationPath = path;
            return path;
        }

        path = Environment.ExpandEnvironmentVariables(path);

        var hasExtension = Path.HasExtension(path);
        var looksLikeFolder = path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);

        if (looksLikeFolder || !hasExtension)
        {
            path = Path.Combine(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), $"reset-rescue-{DateTime.Now:yyyyMMdd-HHmmss}.rrarchive");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        DestinationPath = path;
        return path;
    }

    private string ValidateBackup(string resolvedDestination)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(resolvedDestination))
        {
            errors.Add("Destination path is required.");
        }
        else
        {
            try
            {
                var destDir = Path.GetDirectoryName(resolvedDestination);
                if (string.IsNullOrWhiteSpace(destDir))
                {
                    errors.Add("Provide a valid destination folder.");
                }
                else
                {
                    Directory.CreateDirectory(destDir);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Cannot create destination folder: {ex.Message}");
            }
        }

        return string.Join(" \u2022 ", errors);
    }

    private List<string> BuildSources()
    {
        var sources = new List<string>();

        foreach (var folder in Folders.Where(f => f.IsSelected))
        {
            if (!string.IsNullOrWhiteSpace(folder.Path) && Directory.Exists(folder.Path))
            {
                sources.Add(folder.Path);
            }
        }

        foreach (var app in Apps.Where(a => a.IsSelected))
        {
            foreach (var path in app.DataPaths)
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    sources.Add(path);
                }
            }
        }

        return sources;
    }

    private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableBackupApp.IsSelected))
        {
            UpdateAppSelectionSummary();
        }
    }

    private void UpdateAppSelectionSummary()
    {
        SelectedAppCount = Apps.Count(a => a.IsSelected);
        var names = Apps.Where(a => a.IsSelected)
            .Select(a => a.Display)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(3)
            .ToArray();

        if (SelectedAppCount == 0)
        {
            SelectedAppsPreview = "No apps selected";
            return;
        }

        var tail = SelectedAppCount > names.Length
            ? $" (+{SelectedAppCount - names.Length} more)"
            : string.Empty;

        SelectedAppsPreview = string.Join(", ", names) + tail;
    }

    private void ApplyAppFilter()
    {
        var term = AppSearch?.Trim();
        if (string.IsNullOrEmpty(term))
        {
            foreach (var app in Apps)
            {
                app.IsVisible = true;
            }
            return;
        }

        var lowered = term.ToLowerInvariant();
        foreach (var app in Apps)
        {
            var name = app.Display?.ToLowerInvariant() ?? string.Empty;
            var type = app.Type?.ToLowerInvariant() ?? string.Empty;
            app.IsVisible = name.Contains(lowered) || type.Contains(lowered);
        }
    }

    private Dictionary<string, string>? BuildPathRemapping()
    {
        if (PathMappings.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in PathMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.From) || string.IsNullOrWhiteSpace(mapping.To))
            {
                continue;
            }

            var from = NormalizePath(mapping.From);
            var to = NormalizePath(mapping.To);

            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                continue;
            }

            map[from] = to;
        }

        return map.Count == 0 ? null : map;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!full.EndsWith(System.IO.Path.DirectorySeparatorChar) && !full.EndsWith(System.IO.Path.AltDirectorySeparatorChar))
            {
                full += System.IO.Path.DirectorySeparatorChar;
            }
            return full;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<BackupManifest?> LoadManifestAsync(string archivePath)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(archivePath);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                var entry = zip.GetEntry("manifest.json");
                if (entry == null)
                {
                    return null;
                }

                using var manifestStream = entry.Open();
                using var reader = new StreamReader(manifestStream);
                var json = reader.ReadToEnd();
                return System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(json);
            });
        }
        catch (Exception ex)
        {
            _mainViewModel.LogActivity(ActivityLogLevel.Warning, "ResetRescue", "Failed to read manifest", new[] { ex.Message });
            return null;
        }
    }

    private List<PathMapping> SuggestMappings(BackupManifest manifest)
    {
        var suggestions = new List<PathMapping>();
        var roots = manifest.Entries
            .Select(e => e?.SourcePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => System.IO.Path.GetPathRoot(p!) ?? string.Empty)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Suggest drive-letter swaps (e.g., D: -> C:)
        foreach (var root in roots)
        {
            if (!root.EndsWith(System.IO.Path.DirectorySeparatorChar))
            {
                continue;
            }

            var drive = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (drive.Length == 2 && drive[1] == ':')
            {
                var currentSystemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var currentRoot = System.IO.Path.GetPathRoot(currentSystemDrive);
                if (!string.IsNullOrWhiteSpace(currentRoot) && !string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase))
                {
                    suggestions.Add(new PathMapping(root, currentRoot));
                }
            }
        }

        // Suggest user profile remap if SID or username changed
        var currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var currentProfileRoot = string.IsNullOrWhiteSpace(currentProfile)
            ? null
            : System.IO.Path.GetDirectoryName(currentProfile.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(currentProfileRoot))
        {
            var userSegments = manifest.Entries
                .Select(e => e?.SourcePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 3 && string.Equals(parts[1], "Users", StringComparison.OrdinalIgnoreCase))
                .Select(parts => string.Join(System.IO.Path.DirectorySeparatorChar, parts.Take(3))) // e.g., C:\Users\OldName
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var userRoot in userSegments)
            {
                var normalizedUserRoot = NormalizePath(userRoot);
                if (!string.IsNullOrEmpty(normalizedUserRoot) && !string.Equals(normalizedUserRoot, NormalizePath(currentProfileRoot), StringComparison.OrdinalIgnoreCase))
                {
                    suggestions.Add(new PathMapping(normalizedUserRoot, NormalizePath(currentProfileRoot)));
                }
            }
        }

        return suggestions;
    }

    private void UpsertMapping(string from, string to)
    {
        var existing = PathMappings.FirstOrDefault(m => string.Equals(NormalizePath(m.From), NormalizePath(from), StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.To = to;
            return;
        }

        PathMappings.Add(new PathMapping(from, to));
    }
}

public sealed class PathMapping : ObservableObject
{
    private string _from;
    private string _to;

    public PathMapping()
    {
        _from = string.Empty;
        _to = string.Empty;
    }

    public PathMapping(string from, string to)
    {
        _from = from ?? string.Empty;
        _to = to ?? string.Empty;
    }

    public string From
    {
        get => _from;
        set => SetProperty(ref _from, value ?? string.Empty);
    }

    public string To
    {
        get => _to;
        set => SetProperty(ref _to, value ?? string.Empty);
    }
}

public sealed class SelectableBackupProfile : ObservableObject
{
    public SelectableBackupProfile(BackupProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Paths = profile.KnownFolders ?? Array.Empty<string>();
        _isSelected = true;
    }

    public BackupProfile Profile { get; }
    public IReadOnlyList<string> Paths { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Display => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Sid : Profile.Name;
}

public sealed class SelectableBackupApp : ObservableObject
{
    public SelectableBackupApp(BackupApp app)
    {
        App = app ?? throw new ArgumentNullException(nameof(app));
        DataPaths = app.DataPaths ?? Array.Empty<string>();
        _isSelected = false;
        _isVisible = true;
    }

    public BackupApp App { get; }
    public IReadOnlyList<string> DataPaths { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Display => string.IsNullOrWhiteSpace(App.Name) ? App.Id : App.Name;
    public string Type => App.Type;

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}

public sealed class SelectableBackupFolder : ObservableObject
{
    public SelectableBackupFolder(string owner, string path)
    {
        Owner = owner ?? string.Empty;
        Path = path ?? string.Empty;
        _isSelected = true;
    }

    public string Owner { get; }
    public string Path { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Display => string.IsNullOrWhiteSpace(Path) ? "" : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
}
