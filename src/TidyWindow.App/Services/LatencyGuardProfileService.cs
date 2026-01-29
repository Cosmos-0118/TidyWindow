using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TidyWindow.App.Services;

public sealed class LatencyGuardProfileService : ILatencyGuardProfileService
{
    private const string LogSource = "Latency Guard";
    private const string VisualEffectsKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\VisualEffects";
    private const string DwmKeyPath = "Software\\Microsoft\\Windows\\Dwm";
    private const int PerformanceVisualSetting = 2;

    private static readonly string[] ModelProcessNames =
    {
        "ollama",
        "python",
        "text-generation-webui",
        "invokeai",
        "comfyui",
        "koboldcpp",
        "vllm"
    };

    private readonly ActivityLogService _activityLog;
    private readonly object _gate = new();
    private ProfileSnapshot? _snapshot;
    private LatencyGuardProfileState _state = LatencyGuardProfileState.Inactive;

    public LatencyGuardProfileService(ActivityLogService activityLogService)
    {
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));
    }

    public Task<LatencyGuardProfileState> GetStateAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(_state);
        }
    }

    public Task<LatencyGuardProfileState> ApplyAsync(bool trimEffects, bool trimRefreshRate, bool throttleModels)
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                if (_state.IsApplied)
                {
                    _activityLog.LogInformation(LogSource, "Profile already applied, updating options only.");
                    _state = _state with
                    {
                        EffectsTrimmed = trimEffects,
                        RefreshTrimmed = trimRefreshRate,
                        ModelThrottleActive = throttleModels
                    };

                    return _state;
                }

                var snapshot = new ProfileSnapshot();
                var details = new List<string>();

                if (trimEffects)
                {
                    snapshot.VisualFxSetting = TryReadDword(VisualEffectsKeyPath, "VisualFXSetting");
                    SetVisualFxToPerformance();
                    details.Add($"Visual effects: saved original={snapshot.VisualFxSetting?.ToString() ?? "(not set)"}, set to Performance (2)");
                    details.Add($"Registry: HKCU\\{VisualEffectsKeyPath}\\VisualFXSetting = 2");
                }

                if (trimRefreshRate)
                {
                    snapshot.DynamicRefreshRateSwitching = TryReadDword(DwmKeyPath, "DynamicRefreshRateSwitching");
                    SetDynamicRefreshRateSwitching(0);
                    details.Add($"Dynamic refresh rate: saved original={snapshot.DynamicRefreshRateSwitching?.ToString() ?? "(not set)"}, disabled (0)");
                    details.Add($"Registry: HKCU\\{DwmKeyPath}\\DynamicRefreshRateSwitching = 0");
                }

                snapshot.ModelPriorities = throttleModels ? CaptureProcessPriorities(ModelProcessNames) : new Dictionary<int, ProcessPriorityClass>();
                if (throttleModels)
                {
                    if (snapshot.ModelPriorities.Count > 0)
                    {
                        foreach (var kvp in snapshot.ModelPriorities)
                        {
                            details.Add($"Process throttled: PID={kvp.Key}, original priority={kvp.Value}, set to BelowNormal");
                        }
                    }
                    else
                    {
                        details.Add("No AI model processes found to throttle (ollama, python, comfyui, etc.)");
                    }
                    LowerProcessPriorities(snapshot.ModelPriorities.Keys);
                }

                _snapshot = snapshot;

                _state = new LatencyGuardProfileState(
                    IsApplied: true,
                    EffectsTrimmed: trimEffects,
                    RefreshTrimmed: trimRefreshRate,
                    ModelThrottleActive: throttleModels,
                    AppliedAt: DateTimeOffset.UtcNow);

                _activityLog.LogSuccess(LogSource, "Low-latency profile applied.", details);

                return _state;
            }
        });
    }

    public Task<LatencyGuardProfileState> RevertAsync()
    {
        return Task.Run(() =>
        {
            lock (_gate)
            {
                var snapshot = _snapshot;
                var details = new List<string>();

                if (snapshot is not null)
                {
                    if (snapshot.VisualFxSetting is int fx)
                    {
                        SetVisualFx(fx);
                        details.Add($"Visual effects restored to {fx}");
                        details.Add($"Registry: HKCU\\{VisualEffectsKeyPath}\\VisualFXSetting = {fx}");
                    }

                    if (snapshot.DynamicRefreshRateSwitching is int drs)
                    {
                        SetDynamicRefreshRateSwitching(drs);
                        details.Add($"Dynamic refresh rate restored to {drs}");
                        details.Add($"Registry: HKCU\\{DwmKeyPath}\\DynamicRefreshRateSwitching = {drs}");
                    }

                    if (snapshot.ModelPriorities.Count > 0)
                    {
                        foreach (var kvp in snapshot.ModelPriorities)
                        {
                            details.Add($"Process priority restored: PID={kvp.Key}, priority={kvp.Value}");
                        }
                    }

                    RestoreProcessPriorities(snapshot.ModelPriorities);
                }
                else
                {
                    details.Add("No snapshot found; nothing to revert.");
                }

                _snapshot = null;
                _state = LatencyGuardProfileState.Inactive;

                _activityLog.LogSuccess(LogSource, "Low-latency profile reverted.", details);

                return _state;
            }
        });
    }

    private static Dictionary<int, ProcessPriorityClass> CaptureProcessPriorities(IEnumerable<string> names)
    {
        var map = new Dictionary<int, ProcessPriorityClass>();

        foreach (var name in names)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        map[process.Id] = process.PriorityClass;
                    }
                    catch
                    {
                        // ignore access issues
                    }
                }
            }
            catch
            {
                // ignore lookup issues
            }
        }

        return map;
    }

    private static void LowerProcessPriorities(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // ignore processes that exit or reject priority changes
            }
        }
    }

    private static void RestoreProcessPriorities(Dictionary<int, ProcessPriorityClass> originalPriorities)
    {
        if (originalPriorities.Count == 0)
        {
            return;
        }

        foreach (var kvp in originalPriorities)
        {
            try
            {
                using var process = Process.GetProcessById(kvp.Key);
                process.PriorityClass = kvp.Value;
            }
            catch
            {
                // process may have exited; ignore
            }
        }
    }

    private static int? TryReadDword(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }

    private static void SetVisualFxToPerformance()
    {
        SetVisualFx(PerformanceVisualSetting);
    }

    private static void SetVisualFx(int value)
    {
        TryWriteDword(VisualEffectsKeyPath, "VisualFXSetting", value);
    }

    private static void SetDynamicRefreshRateSwitching(int value)
    {
        TryWriteDword(DwmKeyPath, "DynamicRefreshRateSwitching", value);
    }

    private static void TryWriteDword(string keyPath, string valueName, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key?.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch
        {
            // ignore write failures
        }
    }

    private static void TryWriteDwordHklm(string keyPath, string valueName, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(keyPath);
            key?.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch
        {
            // ignore write failures (may need admin)
        }
    }

    private static int? TryReadDwordHklm(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HAGS (Hardware-Accelerated GPU Scheduling) detection and toggle
    // ─────────────────────────────────────────────────────────────────────────────

    private const string HagsKeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string HagsValueName = "HwSchMode";
    private const int HagsEnabled = 2;
    private const int HagsDisabled = 1;

    public bool? IsHagsEnabled()
    {
        var value = TryReadDwordHklm(HagsKeyPath, HagsValueName);
        return value switch
        {
            HagsEnabled => true,
            HagsDisabled => false,
            _ => null // not set or unknown
        };
    }

    public bool SetHags(bool enabled)
    {
        try
        {
            var previousValue = TryReadDwordHklm(HagsKeyPath, HagsValueName);
            TryWriteDwordHklm(HagsKeyPath, HagsValueName, enabled ? HagsEnabled : HagsDisabled);

            var details = new[]
            {
                $"Registry: HKLM\\{HagsKeyPath}\\{HagsValueName}",
                $"Previous value: {previousValue?.ToString() ?? "(not set)"}",
                $"New value: {(enabled ? HagsEnabled : HagsDisabled)} ({(enabled ? "enabled" : "disabled")})",
                "Note: Reboot required for changes to take effect"
            };

            _activityLog.LogSuccess(LogSource, $"HAGS {(enabled ? "enabled" : "disabled")}.", details);
            return true;
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to set HAGS: {ex.Message}", new[] { ex.ToString() });
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Ollama environment variable configuration
    // ─────────────────────────────────────────────────────────────────────────────

    private const string OllamaNumCtxVar = "OLLAMA_NUM_CTX";
    private const string OllamaNumParallelVar = "OLLAMA_NUM_PARALLEL";

    public OllamaConfig GetOllamaConfig()
    {
        var ctxStr = Environment.GetEnvironmentVariable(OllamaNumCtxVar, EnvironmentVariableTarget.User);
        var parallelStr = Environment.GetEnvironmentVariable(OllamaNumParallelVar, EnvironmentVariableTarget.User);

        int? ctx = int.TryParse(ctxStr, out var c) ? c : null;
        int? parallel = int.TryParse(parallelStr, out var p) ? p : null;

        return new OllamaConfig(ctx, parallel);
    }

    public void SetOllamaConfig(int? numCtx, int? numParallel)
    {
        var details = new List<string>();
        var previousConfig = GetOllamaConfig();

        if (numCtx.HasValue)
        {
            Environment.SetEnvironmentVariable(OllamaNumCtxVar, numCtx.Value.ToString(), EnvironmentVariableTarget.User);
            details.Add($"{OllamaNumCtxVar}: {previousConfig.NumCtx?.ToString() ?? "(not set)"} → {numCtx.Value}");
        }
        else
        {
            Environment.SetEnvironmentVariable(OllamaNumCtxVar, null, EnvironmentVariableTarget.User);
            if (previousConfig.NumCtx.HasValue)
            {
                details.Add($"{OllamaNumCtxVar}: {previousConfig.NumCtx} → (cleared)");
            }
        }

        if (numParallel.HasValue)
        {
            Environment.SetEnvironmentVariable(OllamaNumParallelVar, numParallel.Value.ToString(), EnvironmentVariableTarget.User);
            details.Add($"{OllamaNumParallelVar}: {previousConfig.NumParallel?.ToString() ?? "(not set)"} → {numParallel.Value}");
        }
        else
        {
            Environment.SetEnvironmentVariable(OllamaNumParallelVar, null, EnvironmentVariableTarget.User);
            if (previousConfig.NumParallel.HasValue)
            {
                details.Add($"{OllamaNumParallelVar}: {previousConfig.NumParallel} → (cleared)");
            }
        }

        details.Add("Environment: User-level environment variables updated");
        _activityLog.LogSuccess(LogSource, "Ollama configuration updated.", details);
    }

    public bool IsOllamaRunning()
    {
        try
        {
            return Process.GetProcessesByName("ollama").Length > 0
                || Process.GetProcessesByName("ollama_llama_server").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void RestartOllama()
    {
        var details = new List<string>();

        try
        {
            // Kill existing Ollama processes
            var killedProcesses = new List<string>();
            foreach (var proc in Process.GetProcessesByName("ollama"))
            {
                try
                {
                    killedProcesses.Add($"ollama (PID={proc.Id})");
                    proc.Kill();
                }
                catch { }
            }
            foreach (var proc in Process.GetProcessesByName("ollama_llama_server"))
            {
                try
                {
                    killedProcesses.Add($"ollama_llama_server (PID={proc.Id})");
                    proc.Kill();
                }
                catch { }
            }

            if (killedProcesses.Count > 0)
            {
                details.Add($"Killed processes: {string.Join(", ", killedProcesses)}");
            }

            // Start Ollama serve in background
            var psi = new ProcessStartInfo
            {
                FileName = "ollama",
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var newProc = Process.Start(psi);
            if (newProc is not null)
            {
                details.Add($"Started: ollama serve (PID={newProc.Id})");
            }

            _activityLog.LogSuccess(LogSource, "Ollama restarted with new configuration.", details);
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to restart Ollama: {ex.Message}", new[] { ex.ToString() });
        }
    }

    private sealed class ProfileSnapshot
    {
        public int? VisualFxSetting { get; set; }

        public int? DynamicRefreshRateSwitching { get; set; }

        public Dictionary<int, ProcessPriorityClass> ModelPriorities { get; set; } = new();
    }
}

public record OllamaConfig(int? NumCtx, int? NumParallel);

public record LatencyGuardProfileState(
    bool IsApplied,
    bool EffectsTrimmed,
    bool RefreshTrimmed,
    bool ModelThrottleActive,
    DateTimeOffset? AppliedAt)
{
    public static LatencyGuardProfileState Inactive { get; } = new(false, false, false, false, null);
}
