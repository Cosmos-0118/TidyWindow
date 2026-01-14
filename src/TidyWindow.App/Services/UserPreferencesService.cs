using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyWindow.App.Services;

public sealed class UserPreferencesService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    private readonly object _sync = new();
    private readonly string _storagePath;
    private UserPreferences _preferences;

    public UserPreferencesService()
    {
        var baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tidyFolder = Path.Combine(baseFolder, "TidyWindow");
        Directory.CreateDirectory(tidyFolder);

        _storagePath = Path.Combine(tidyFolder, "preferences.json");
        _preferences = Normalize(LoadPreferences());
    }

    public event EventHandler<UserPreferencesChangedEventArgs>? PreferencesChanged;

    public UserPreferences Current
    {
        get
        {
            lock (_sync)
            {
                return _preferences;
            }
        }
    }

    public void SetRunInBackground(bool value) => Update(p => p with { RunInBackground = value });

    public void SetLaunchAtStartup(bool value) => Update(p => p with { LaunchAtStartup = value });

    public void SetPulseGuardEnabled(bool value) => Update(p => p with { PulseGuardEnabled = value });

    public void SetStartupGuardEnabled(bool value) => Update(p => p with { StartupGuardEnabled = value });

    public void SetShowStartupHero(bool value) => Update(p => p with { ShowStartupHero = value });

    public void SetNotificationsEnabled(bool value) => Update(p => p with { NotificationsEnabled = value });

    public void SetNotifyOnlyWhenInactive(bool value) => Update(p => p with { NotifyOnlyWhenInactive = value });

    public void SetShowSuccessSummaries(bool value) => Update(p => p with { PulseGuardShowSuccessSummaries = value });

    public void SetShowActionAlerts(bool value) => Update(p => p with { PulseGuardShowActionAlerts = value });

    public MaintenanceSuppressionEntry? GetMaintenanceSuppression(string? manager, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(manager) || string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var current = Current;
        var suppressions = current.MaintenanceSuppressions;
        if (suppressions is null || suppressions.Length == 0)
        {
            return null;
        }

        var targetManager = NormalizeManager(manager);
        var targetPackage = NormalizePackageId(packageId);

        foreach (var entry in suppressions)
        {
            if (MatchesKey(entry, targetManager, targetPackage))
            {
                return entry;
            }
        }

        return null;
    }

    public MaintenanceSuppressionEntry AddMaintenanceSuppression(
        string manager,
        string packageId,
        string reason,
        string message,
        int exitCode,
        string? latestKnownVersion,
        string? requestedVersion)
    {
        var normalizedManager = NormalizeManager(manager);
        var normalizedPackage = NormalizePackageId(packageId);
        if (normalizedManager.Length == 0 || normalizedPackage.Length == 0)
        {
            throw new ArgumentException("Manager and package identifier must be provided.");
        }

        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Manual action required." : message.Trim();

        Update(preferences =>
        {
            var suppressionsSource = preferences.MaintenanceSuppressions;
            var suppressions = suppressionsSource is { Length: > 0 }
                ? suppressionsSource.ToList()
                : new List<MaintenanceSuppressionEntry>();
            var entry = new MaintenanceSuppressionEntry(
                normalizedManager,
                normalizedPackage,
                string.IsNullOrWhiteSpace(reason) ? MaintenanceSuppressionReasons.Unknown : reason.Trim(),
                normalizedMessage,
                exitCode,
                NormalizeVersion(latestKnownVersion),
                NormalizeVersion(requestedVersion),
                DateTimeOffset.UtcNow);

            var index = suppressions.FindIndex(existing => MatchesKey(existing, normalizedManager, normalizedPackage));
            if (index >= 0)
            {
                if (suppressions[index] == entry)
                {
                    return preferences;
                }

                suppressions[index] = entry;
            }
            else
            {
                suppressions.Add(entry);
            }

            return preferences with { MaintenanceSuppressions = suppressions.ToArray() };
        });

        return GetMaintenanceSuppression(normalizedManager, normalizedPackage)
               ?? new MaintenanceSuppressionEntry(
                   normalizedManager,
                   normalizedPackage,
                   string.IsNullOrWhiteSpace(reason) ? MaintenanceSuppressionReasons.Unknown : reason.Trim(),
                   normalizedMessage,
                   exitCode,
                   NormalizeVersion(latestKnownVersion),
                   NormalizeVersion(requestedVersion),
                   DateTimeOffset.UtcNow);
    }

    public bool RemoveMaintenanceSuppression(string? manager, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(manager) || string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        var normalizedManager = NormalizeManager(manager);
        var normalizedPackage = NormalizePackageId(packageId);
        if (normalizedManager.Length == 0 || normalizedPackage.Length == 0)
        {
            return false;
        }

        var removed = false;

        Update(preferences =>
        {
            var suppressionsSource = preferences.MaintenanceSuppressions;
            if (suppressionsSource is null || suppressionsSource.Length == 0)
            {
                return preferences;
            }

            var suppressions = suppressionsSource.ToList();
            var before = suppressions.Count;
            suppressions.RemoveAll(entry => MatchesKey(entry, normalizedManager, normalizedPackage));
            removed = suppressions.Count != before;

            return removed
                ? preferences with { MaintenanceSuppressions = suppressions.ToArray() }
                : preferences;
        });

        return removed;
    }

    private void Update(Func<UserPreferences, UserPreferences> mutator)
    {
        UserPreferences previous;
        UserPreferences updated;

        lock (_sync)
        {
            previous = _preferences;
            updated = Normalize(mutator(previous));
            if (updated == previous)
            {
                return;
            }

            _preferences = updated;
            SavePreferences(updated);
        }

        PreferencesChanged?.Invoke(this, new UserPreferencesChangedEventArgs(updated, previous));
    }

    private UserPreferences LoadPreferences()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var loaded = JsonSerializer.Deserialize<UserPreferences>(json, SerializerOptions);
                if (loaded is not null)
                {
                    return Normalize(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load preferences: {ex.Message}");
        }

        return Normalize(new UserPreferences());
    }

    private void SavePreferences(UserPreferences preferences)
    {
        try
        {
            var json = JsonSerializer.Serialize(Normalize(preferences), SerializerOptions);
            File.WriteAllText(_storagePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist preferences: {ex.Message}");
        }
    }

    private static UserPreferences Normalize(UserPreferences preferences)
    {
        var suppressions = preferences.MaintenanceSuppressions ?? Array.Empty<MaintenanceSuppressionEntry>();
        if (suppressions.Length > 0)
        {
            var map = new Dictionary<string, MaintenanceSuppressionEntry>(KeyComparer);
            foreach (var entry in suppressions)
            {
                if (entry is null)
                {
                    continue;
                }

                var manager = NormalizeManager(entry.Manager);
                var packageId = NormalizePackageId(entry.PackageId);
                if (manager.Length == 0 || packageId.Length == 0)
                {
                    continue;
                }

                var normalized = entry with
                {
                    Manager = manager,
                    PackageId = packageId,
                    Reason = string.IsNullOrWhiteSpace(entry.Reason) ? MaintenanceSuppressionReasons.Unknown : entry.Reason.Trim(),
                    Message = string.IsNullOrWhiteSpace(entry.Message) ? "Manual action required." : entry.Message.Trim(),
                    LatestKnownVersion = NormalizeVersion(entry.LatestKnownVersion),
                    RequestedVersion = NormalizeVersion(entry.RequestedVersion)
                };

                var key = BuildKey(manager, packageId);
                if (map.TryGetValue(key, out var existing))
                {
                    map[key] = existing.CreatedAt >= normalized.CreatedAt ? existing : normalized;
                }
                else
                {
                    map[key] = normalized;
                }
            }

            suppressions = map.Values
                .OrderByDescending(entry => entry.CreatedAt)
                .ToArray();
        }

        if (ReferenceEquals(suppressions, preferences.MaintenanceSuppressions)
            && suppressions.Length == 0)
        {
            suppressions = Array.Empty<MaintenanceSuppressionEntry>();
        }

        if (!ReferenceEquals(suppressions, preferences.MaintenanceSuppressions))
        {
            preferences = preferences with { MaintenanceSuppressions = suppressions };
        }
        else if (preferences.MaintenanceSuppressions is null)
        {
            preferences = preferences with { MaintenanceSuppressions = Array.Empty<MaintenanceSuppressionEntry>() };
        }

        return preferences;
    }

    private static bool MatchesKey(MaintenanceSuppressionEntry entry, string manager, string packageId)
    {
        return KeyComparer.Equals(entry.Manager, manager) && KeyComparer.Equals(entry.PackageId, packageId);
    }

    private static string NormalizeManager(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string NormalizePackageId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = trimmed.Replace('_', '.').Replace('-', '.');
        while (candidate.Contains("..", StringComparison.Ordinal))
        {
            candidate = candidate.Replace("..", ".");
        }

        return candidate.Trim('.');
    }

    private static string BuildKey(string manager, string packageId)
    {
        return manager + "\u001f" + packageId;
    }
}

public sealed record UserPreferences(
    bool RunInBackground = false,
    bool LaunchAtStartup = true,
    bool PulseGuardEnabled = true,
    bool StartupGuardEnabled = false,
    bool ShowStartupHero = true,
    bool NotificationsEnabled = true,
    bool NotifyOnlyWhenInactive = true,
    bool PulseGuardShowSuccessSummaries = true,
    bool PulseGuardShowActionAlerts = true,
    MaintenanceSuppressionEntry[]? MaintenanceSuppressions = null);

public sealed class UserPreferencesChangedEventArgs : EventArgs
{
    public UserPreferencesChangedEventArgs(UserPreferences preferences, UserPreferences previous)
    {
        Preferences = preferences;
        Previous = previous;
    }

    public UserPreferences Preferences { get; }

    public UserPreferences Previous { get; }
}

public sealed record MaintenanceSuppressionEntry(
    string Manager,
    string PackageId,
    string Reason,
    string Message,
    int ExitCode,
    string? LatestKnownVersion,
    string? RequestedVersion,
    DateTimeOffset CreatedAt);

public static class MaintenanceSuppressionReasons
{
    public const string Unknown = "Unknown";
    public const string ManualUpgradeRequired = "ManualUpgradeRequired";
}
