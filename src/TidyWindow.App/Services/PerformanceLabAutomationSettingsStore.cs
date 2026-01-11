using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.App.Services;

/// <summary>
/// Persists Performance Lab boot automation settings to disk.
/// </summary>
public sealed class PerformanceLabAutomationSettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    public PerformanceLabAutomationSettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "performance-lab-automation.json");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public PerformanceLabAutomationSettings Get()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return PerformanceLabAutomationSettings.Default;
                }

                using var stream = File.OpenRead(_filePath);
                var payload = JsonSerializer.Deserialize<StatePayload>(stream, _serializerOptions);
                return payload?.ToSettings() ?? PerformanceLabAutomationSettings.Default;
            }
            catch
            {
                return PerformanceLabAutomationSettings.Default;
            }
        }
    }

    public void Save(PerformanceLabAutomationSettings settings)
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
                // Persistence failures should not bubble up into the UI layer.
            }
        }
    }

    private sealed class StatePayload
    {
        public bool AutomationEnabled { get; set; }

        public long LastBootMarker { get; set; }

        public DateTimeOffset? LastRunUtc { get; set; }

        public PerformanceLabSnapshotPayload Snapshot { get; set; } = new();

        public PerformanceLabAutomationSettings ToSettings()
        {
            return new PerformanceLabAutomationSettings(
                AutomationEnabled,
                LastBootMarker,
                LastRunUtc,
                Snapshot?.ToSnapshot() ?? PerformanceLabAutomationSnapshot.Empty);
        }

        public static StatePayload FromSettings(PerformanceLabAutomationSettings settings)
        {
            return new StatePayload
            {
                AutomationEnabled = settings.AutomationEnabled,
                LastBootMarker = settings.LastBootMarker,
                LastRunUtc = settings.LastRunUtc,
                Snapshot = PerformanceLabSnapshotPayload.FromSnapshot(settings.Snapshot)
            };
        }
    }

    private sealed class PerformanceLabSnapshotPayload
    {
        public bool ApplyUltimatePlan { get; set; }
        public bool ApplyServiceTemplate { get; set; }
        public bool ApplyHardwareFix { get; set; }
        public bool ApplyKernelPreset { get; set; }
        public bool ApplyVbsDisable { get; set; }
        public bool ApplyEtwCleanup { get; set; }
        public bool ApplyPagefilePreset { get; set; }
        public bool ApplySchedulerPreset { get; set; }
        public bool ApplyIoBoosts { get; set; }
        public bool ApplyAutoTune { get; set; }
        public string? ServiceTemplateId { get; set; }
        public string? PagefilePresetId { get; set; }
        public string? TargetPagefileDrive { get; set; }
        public int? PagefileInitialMb { get; set; }
        public int? PagefileMaxMb { get; set; }
        public bool RunWorkingSetSweep { get; set; }
        public bool SweepPinnedApps { get; set; }
        public string? SchedulerPresetId { get; set; }
        public string? SchedulerProcessNames { get; set; }
        public bool BoostIoPriority { get; set; }
        public bool BoostThreadPriority { get; set; }
        public string? AutoTuneProcessNames { get; set; }
        public string? AutoTunePresetId { get; set; }
        public string? EtwMode { get; set; }

        public PerformanceLabAutomationSnapshot ToSnapshot()
        {
            return new PerformanceLabAutomationSnapshot(
                ApplyUltimatePlan,
                ApplyServiceTemplate,
                ApplyHardwareFix,
                ApplyKernelPreset,
                ApplyVbsDisable,
                ApplyEtwCleanup,
                ApplyPagefilePreset,
                ApplySchedulerPreset,
                ApplyIoBoosts,
                ApplyAutoTune,
                ServiceTemplateId ?? "Balanced",
                PagefilePresetId ?? "SystemManaged",
                TargetPagefileDrive ?? "C:",
                PagefileInitialMb,
                PagefileMaxMb,
                RunWorkingSetSweep,
                SweepPinnedApps,
                SchedulerPresetId ?? "Balanced",
                SchedulerProcessNames ?? string.Empty,
                BoostIoPriority,
                BoostThreadPriority,
                AutoTuneProcessNames ?? string.Empty,
                AutoTunePresetId ?? "LatencyBoost",
                EtwMode ?? "Minimal");
        }

        public static PerformanceLabSnapshotPayload FromSnapshot(PerformanceLabAutomationSnapshot snapshot)
        {
            return new PerformanceLabSnapshotPayload
            {
                ApplyUltimatePlan = snapshot.ApplyUltimatePlan,
                ApplyServiceTemplate = snapshot.ApplyServiceTemplate,
                ApplyHardwareFix = snapshot.ApplyHardwareFix,
                ApplyKernelPreset = snapshot.ApplyKernelPreset,
                ApplyVbsDisable = snapshot.ApplyVbsDisable,
                ApplyEtwCleanup = snapshot.ApplyEtwCleanup,
                ApplyPagefilePreset = snapshot.ApplyPagefilePreset,
                ApplySchedulerPreset = snapshot.ApplySchedulerPreset,
                ApplyIoBoosts = snapshot.ApplyIoBoosts,
                ApplyAutoTune = snapshot.ApplyAutoTune,
                ServiceTemplateId = snapshot.ServiceTemplateId,
                PagefilePresetId = snapshot.PagefilePresetId,
                TargetPagefileDrive = snapshot.TargetPagefileDrive,
                PagefileInitialMb = snapshot.PagefileInitialMb,
                PagefileMaxMb = snapshot.PagefileMaxMb,
                RunWorkingSetSweep = snapshot.RunWorkingSetSweep,
                SweepPinnedApps = snapshot.SweepPinnedApps,
                SchedulerPresetId = snapshot.SchedulerPresetId,
                SchedulerProcessNames = snapshot.SchedulerProcessNames,
                BoostIoPriority = snapshot.BoostIoPriority,
                BoostThreadPriority = snapshot.BoostThreadPriority,
                AutoTuneProcessNames = snapshot.AutoTuneProcessNames,
                AutoTunePresetId = snapshot.AutoTunePresetId,
                EtwMode = snapshot.EtwMode
            };
        }
    }
}