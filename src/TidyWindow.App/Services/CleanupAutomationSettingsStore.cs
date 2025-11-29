using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TidyWindow.Core.Cleanup;

namespace TidyWindow.App.Services;

/// <summary>
/// Persists cleanup automation settings on disk.
/// </summary>
public sealed class CleanupAutomationSettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    public CleanupAutomationSettingsStore()
        : this(customRootPath: null)
    {
    }

    public CleanupAutomationSettingsStore(string? customRootPath)
    {
        var root = string.IsNullOrWhiteSpace(customRootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow")
            : customRootPath;
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "cleanup-automation.json");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public CleanupAutomationSettings Get()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return CleanupAutomationSettings.Default;
                }

                using var stream = File.OpenRead(_filePath);
                var payload = JsonSerializer.Deserialize<StatePayload>(stream, _serializerOptions);
                return payload?.ToSettings() ?? CleanupAutomationSettings.Default;
            }
            catch
            {
                return CleanupAutomationSettings.Default;
            }
        }
    }

    public void Save(CleanupAutomationSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        lock (_syncRoot)
        {
            try
            {
                var payload = StatePayload.FromSettings(settings.Normalize());
                var tempPath = _filePath + ".tmp";

                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, payload, _serializerOptions);
                    stream.Flush(true);
                }

                File.Copy(tempPath, _filePath, overwrite: true);
                File.Delete(tempPath);
            }
            catch
            {
                // Persistence errors are non-fatal.
            }
        }
    }

    private sealed class StatePayload
    {
        public bool AutomationEnabled { get; set; }

        public int IntervalMinutes { get; set; }

        public CleanupAutomationDeletionMode DeletionMode { get; set; }

        public bool IncludeDownloads { get; set; }

        public bool IncludeBrowserHistory { get; set; }

        public int TopItemCount { get; set; }

        public DateTimeOffset? LastRunUtc { get; set; }

        public CleanupAutomationSettings ToSettings()
        {
            return new CleanupAutomationSettings(
                AutomationEnabled,
                IntervalMinutes,
                DeletionMode,
                IncludeDownloads,
                IncludeBrowserHistory,
                TopItemCount,
                LastRunUtc);
        }

        public static StatePayload FromSettings(CleanupAutomationSettings settings)
        {
            return new StatePayload
            {
                AutomationEnabled = settings.AutomationEnabled,
                IntervalMinutes = settings.IntervalMinutes,
                DeletionMode = settings.DeletionMode,
                IncludeDownloads = settings.IncludeDownloads,
                IncludeBrowserHistory = settings.IncludeBrowserHistory,
                TopItemCount = settings.TopItemCount,
                LastRunUtc = settings.LastRunUtc
            };
        }
    }
}