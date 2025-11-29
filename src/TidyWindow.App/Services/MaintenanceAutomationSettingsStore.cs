using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Services;

/// <summary>
/// Persists maintenance auto-update settings to disk.
/// </summary>
public sealed class MaintenanceAutomationSettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    public MaintenanceAutomationSettingsStore(string? rootPath = null)
    {
        var root = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow")
            : rootPath;

        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "maintenance-automation.json");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public MaintenanceAutomationSettings Get()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return MaintenanceAutomationSettings.Default;
                }

                using var stream = File.OpenRead(_filePath);
                var payload = JsonSerializer.Deserialize<StatePayload>(stream, _serializerOptions);
                return payload?.ToSettings() ?? MaintenanceAutomationSettings.Default;
            }
            catch
            {
                return MaintenanceAutomationSettings.Default;
            }
        }
    }

    public void Save(MaintenanceAutomationSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        lock (_syncRoot)
        {
            try
            {
                var payload = StatePayload.FromSettings(settings);
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
                // Persistence failures should not block the UI flow.
            }
        }
    }

    private sealed class StatePayload
    {
        public bool AutomationEnabled { get; set; }

        public bool UpdateAllPackages { get; set; }

        public int IntervalMinutes { get; set; }

        public DateTimeOffset? LastRunUtc { get; set; }

        public List<TargetPayload> Targets { get; set; } = new();

        public MaintenanceAutomationSettings ToSettings()
        {
            var targetRecords = Targets?.Select(payload => payload.ToTarget())
                .Where(static target => target is not null)
                .Cast<MaintenanceAutomationTarget>()
                .ToImmutableArray() ?? ImmutableArray<MaintenanceAutomationTarget>.Empty;

            return new MaintenanceAutomationSettings(
                AutomationEnabled,
                UpdateAllPackages,
                IntervalMinutes,
                LastRunUtc,
                targetRecords);
        }

        public static StatePayload FromSettings(MaintenanceAutomationSettings settings)
        {
            var payload = new StatePayload
            {
                AutomationEnabled = settings.AutomationEnabled,
                UpdateAllPackages = settings.UpdateAllPackages,
                IntervalMinutes = settings.IntervalMinutes,
                LastRunUtc = settings.LastRunUtc,
                Targets = settings.Targets.IsDefault
                    ? new List<TargetPayload>()
                    : settings.Targets.Select(TargetPayload.FromTarget).ToList()
            };

            return payload;
        }
    }

    private sealed class TargetPayload
    {
        public string? Manager { get; set; }

        public string? PackageId { get; set; }

        public string? Label { get; set; }

        public MaintenanceAutomationTarget? ToTarget()
        {
            if (string.IsNullOrWhiteSpace(Manager) || string.IsNullOrWhiteSpace(PackageId))
            {
                return null;
            }

            var target = new MaintenanceAutomationTarget(Manager, PackageId, Label);
            return target.IsValid ? target : null;
        }

        public static TargetPayload FromTarget(MaintenanceAutomationTarget target)
        {
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            return new TargetPayload
            {
                Manager = target.Manager,
                PackageId = target.PackageId,
                Label = target.Label
            };
        }
    }
}
