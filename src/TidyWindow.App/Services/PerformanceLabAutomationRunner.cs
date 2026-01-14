using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Performance;

namespace TidyWindow.App.Services;

/// <summary>
/// Replays selected Performance Lab steps automatically after reboot using the last-known presets.
/// </summary>
public sealed class PerformanceLabAutomationRunner : IDisposable
{
    private const string UltimateSchemeGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private static readonly Regex GuidRegex = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly PerformanceLabAutomationSettingsStore _store;
    private readonly IPerformanceLabService _service;
    private readonly ActivityLogService _activityLog;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private PerformanceLabAutomationSettings _settings;
    private bool _disposed;

    public PerformanceLabAutomationRunner(
        PerformanceLabAutomationSettingsStore store,
        IPerformanceLabService service,
        ActivityLogService activityLog,
        IAutomationWorkTracker workTracker)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        _settings = _store.Get().Normalize();
        _ = RunIfDueAsync();
    }

    public PerformanceLabAutomationSettings CurrentSettings => _settings;

    public event EventHandler<PerformanceLabAutomationSettings>? SettingsChanged;

    public async Task<PerformanceLabAutomationRunResult?> ApplySettingsAsync(PerformanceLabAutomationSettings settings, bool runIfDue, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var normalized = settings.Normalize();
        _settings = normalized;
        _store.Save(normalized);
        OnSettingsChanged();

        if (runIfDue && ShouldRunOnCurrentBoot(normalized))
        {
            return await RunInternalAsync(normalized, isBackground: false, ignoreBootCheck: false, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<PerformanceLabAutomationRunResult?> RunIfDueAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!ShouldRunOnCurrentBoot(_settings))
        {
            return Task.FromResult<PerformanceLabAutomationRunResult?>(null);
        }

        return RunInternalAsync(_settings, isBackground: true, ignoreBootCheck: false, cancellationToken)
            .ContinueWith<PerformanceLabAutomationRunResult?>(static task => task.Result, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task<PerformanceLabAutomationRunResult> RunNowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunInternalAsync(_settings, isBackground: false, ignoreBootCheck: true, cancellationToken);
    }

    public long GetCurrentBootMarker() => ComputeBootMarker();

    private bool ShouldRunOnCurrentBoot(PerformanceLabAutomationSettings settings)
    {
        if (!settings.AutomationEnabled || settings.Snapshot is null || !settings.Snapshot.HasActions)
        {
            return false;
        }

        var currentBoot = ComputeBootMarker();
        return currentBoot != settings.LastBootMarker;
    }

    private async Task<PerformanceLabAutomationRunResult> RunInternalAsync(PerformanceLabAutomationSettings settings, bool isBackground, bool ignoreBootCheck, CancellationToken cancellationToken)
    {
        if (!ignoreBootCheck && !ShouldRunOnCurrentBoot(settings))
        {
            return PerformanceLabAutomationRunResult.Skipped(DateTimeOffset.UtcNow, ComputeBootMarker(), "Not scheduled for this boot.");
        }

        var timeout = isBackground ? 0 : Timeout.Infinite;
        if (!await _runLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return PerformanceLabAutomationRunResult.Skipped(DateTimeOffset.UtcNow, ComputeBootMarker(), "Automation already running.");
        }

        Guid workToken = Guid.Empty;
        var actions = new List<PerformanceLabAutomationActionResult>();
        var bootMarker = ComputeBootMarker();

        try
        {
            workToken = _workTracker.BeginWork(AutomationWorkType.Performance, "Performance Lab boot automation");
            actions.AddRange(await RunStepsAsync(settings.Snapshot, cancellationToken).ConfigureAwait(false));

            var timestamp = DateTimeOffset.UtcNow;
            _settings = settings.WithRun(timestamp, bootMarker).Normalize();
            _store.Save(_settings);
            OnSettingsChanged();

            var result = PerformanceLabAutomationRunResult.Completed(timestamp, bootMarker, actions);
            LogRunResult(result);
            return result;
        }
        catch (Exception ex)
        {
            var failure = new PerformanceLabAutomationActionResult("Unhandled", false, ex.Message);
            actions.Add(failure);
            var timestamp = DateTimeOffset.UtcNow;
            var result = PerformanceLabAutomationRunResult.Completed(timestamp, bootMarker, actions);
            _activityLog.LogError("Performance Lab automation", "Automation failed", new[] { ex.ToString() });
            return result;
        }
        finally
        {
            if (workToken != Guid.Empty)
            {
                _workTracker.CompleteWork(workToken);
            }

            _runLock.Release();
        }
    }

    private async Task<IReadOnlyList<PerformanceLabAutomationActionResult>> RunStepsAsync(PerformanceLabAutomationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var actions = new List<PerformanceLabAutomationActionResult>();

        async Task AddStepAsync(string name, Func<Task<PowerShellInvocationResult>> action)
        {
            var result = await InvokeSafelyAsync(action, cancellationToken).ConfigureAwait(false);
            actions.Add(MapResult(name, result));
        }

        if (snapshot.ApplyUltimatePlan)
        {
            actions.Add(await ApplyUltimatePlanWithVerificationAsync(cancellationToken).ConfigureAwait(false));
        }

        if (snapshot.ApplyServiceTemplate)
        {
            var template = string.IsNullOrWhiteSpace(snapshot.ServiceTemplateId) ? "Balanced" : snapshot.ServiceTemplateId;
            await AddStepAsync("Service template", () => _service.ApplyServiceSlimmingAsync(template, cancellationToken)).ConfigureAwait(false);
        }

        if (snapshot.ApplyHardwareFix)
        {
            await AddStepAsync("Hardware reserved fix", () => _service.ApplyHardwareReservedFixAsync(cancellationToken)).ConfigureAwait(false);
        }

        if (snapshot.ApplyKernelPreset)
        {
            await AddStepAsync("Kernel preset", () => _service.ApplyKernelBootActionAsync("Recommended", skipRestorePoint: true, cancellationToken)).ConfigureAwait(false);
        }

        if (snapshot.ApplyVbsDisable)
        {
            await AddStepAsync("Disable VBS/HVCI", () => _service.DisableVbsHvciAsync(cancellationToken)).ConfigureAwait(false);
        }

        if (snapshot.ApplyEtwCleanup)
        {
            var mode = string.IsNullOrWhiteSpace(snapshot.EtwMode) ? "Minimal" : snapshot.EtwMode;
            await AddStepAsync("ETW cleanup", () => _service.CleanupEtwTracingAsync(mode, cancellationToken)).ConfigureAwait(false);
        }

        if (snapshot.ApplySchedulerPreset)
        {
            var preset = string.IsNullOrWhiteSpace(snapshot.SchedulerPresetId) ? "Balanced" : snapshot.SchedulerPresetId;
            var processes = snapshot.SchedulerProcessNames ?? string.Empty;
            await AddStepAsync("Scheduler preset", () => _service.ApplySchedulerAffinityAsync(preset, processes, cancellationToken)).ConfigureAwait(false);
        }

        if (snapshot.ApplyAutoTune)
        {
            var preset = string.IsNullOrWhiteSpace(snapshot.AutoTunePresetId) ? "LatencyBoost" : snapshot.AutoTunePresetId;
            var processes = snapshot.AutoTuneProcessNames ?? string.Empty;
            await AddStepAsync("Auto-tune watcher", () => _service.StartAutoTuneAsync(processes, preset, cancellationToken)).ConfigureAwait(false);
        }

        return actions;
    }

    private static PerformanceLabAutomationActionResult MapResult(string name, PowerShellInvocationResult result)
    {
        if (result is null)
        {
            return new PerformanceLabAutomationActionResult(name, false, "No result returned.");
        }

        if (result.IsSuccess)
        {
            var summary = result.Output?.FirstOrDefault() ?? "Completed";
            return new PerformanceLabAutomationActionResult(name, true, summary);
        }

        var error = result.Errors?.FirstOrDefault() ?? "Operation failed.";
        return new PerformanceLabAutomationActionResult(name, false, error);
    }

    private async Task<PerformanceLabAutomationActionResult> ApplyUltimatePlanWithVerificationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var steps = new List<string>();

        async Task<bool> DetectAsync(string label)
        {
            var status = await _service.GetPowerPlanStatusAsync(cancellationToken).ConfigureAwait(false);
            var name = string.IsNullOrWhiteSpace(status.ActiveSchemeName) ? "unknown" : status.ActiveSchemeName;
            steps.Add($"{label}: {(status.IsUltimateActive ? "Ultimate active" : name)}");
            return status.IsUltimateActive;
        }

        if (await DetectAsync("Detect before apply").ConfigureAwait(false))
        {
            return new PerformanceLabAutomationActionResult("Ultimate Performance plan", true, "Ultimate plan already active.");
        }

        async Task<bool> TryPrimaryAsync()
        {
            var primary = await InvokeSafelyAsync(() => _service.EnableUltimatePowerPlanAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            steps.Add(primary.IsSuccess
                ? "Primary enable: success"
                : $"Primary enable failed: {primary.Errors?.FirstOrDefault() ?? "unknown"}");

            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            return await DetectAsync("Detect after primary").ConfigureAwait(false);
        }

        async Task<bool> TryForceAsync()
        {
            var forced = await ForceUltimatePlanAsync(cancellationToken).ConfigureAwait(false);
            steps.Add(forced.Message);
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            return await DetectAsync("Detect after force").ConfigureAwait(false);
        }

        async Task<bool> TryDuplicateAsync()
        {
            var duplicate = await RunPowerCfgAsync($"-duplicatescheme {UltimateSchemeGuid}", cancellationToken).ConfigureAwait(false);
            var dupGuid = GuidRegex.Match(duplicate.Output ?? string.Empty).Value;
            var target = string.IsNullOrWhiteSpace(dupGuid) ? UltimateSchemeGuid : dupGuid;
            var activate = await RunPowerCfgAsync($"-setactive {target}", cancellationToken).ConfigureAwait(false);

            steps.Add($"Duplicate attempt exit {duplicate.ExitCode}; activate exit {activate.ExitCode}");
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            return await DetectAsync("Detect after duplicate").ConfigureAwait(false);
        }

        if (await TryPrimaryAsync().ConfigureAwait(false))
        {
            return new PerformanceLabAutomationActionResult("Ultimate Performance plan", true, string.Join(" | ", steps));
        }

        if (await TryForceAsync().ConfigureAwait(false))
        {
            return new PerformanceLabAutomationActionResult("Ultimate Performance plan", true, string.Join(" | ", steps));
        }

        if (await TryDuplicateAsync().ConfigureAwait(false))
        {
            return new PerformanceLabAutomationActionResult("Ultimate Performance plan", true, string.Join(" | ", steps));
        }

        return new PerformanceLabAutomationActionResult("Ultimate Performance plan", false, string.Join(" | ", steps));
    }

    private async Task<PerformanceLabAutomationActionResult> ForceUltimatePlanAsync(CancellationToken cancellationToken)
    {
        try
        {
            var activate = await RunPowerCfgAsync($"-setactive {UltimateSchemeGuid}", cancellationToken).ConfigureAwait(false);

            if (activate.ExitCode != 0)
            {
                var duplicate = await RunPowerCfgAsync($"-duplicatescheme {UltimateSchemeGuid}", cancellationToken).ConfigureAwait(false);
                var duplicatedGuid = GuidRegex.Match(duplicate.Output ?? string.Empty).Value;
                var target = string.IsNullOrWhiteSpace(duplicatedGuid) ? UltimateSchemeGuid : duplicatedGuid;
                activate = await RunPowerCfgAsync($"-setactive {target}", cancellationToken).ConfigureAwait(false);
            }

            var status = await _service.GetPowerPlanStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.IsUltimateActive)
            {
                var message = activate.ExitCode == 0 ? "Ultimate plan forced via powercfg." : "Ultimate plan forced after duplicate creation.";
                return new PerformanceLabAutomationActionResult("Ultimate Performance plan", true, message);
            }

            var errorMessage = string.IsNullOrWhiteSpace(activate.Errors)
                ? "powercfg fallback failed to activate Ultimate plan."
                : activate.Errors.Trim();

            return new PerformanceLabAutomationActionResult("Ultimate Performance plan", false, errorMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PerformanceLabAutomationActionResult("Ultimate Performance plan", false, ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output, string Errors)> RunPowerCfgAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var errors = await errorTask.ConfigureAwait(false);

        return (process.ExitCode, output, errors);
    }

    private static async Task<PowerShellInvocationResult> InvokeSafelyAsync(Func<Task<PowerShellInvocationResult>> action, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PowerShellInvocationResult(Array.Empty<string>(), new[] { ex.Message }, 1);
        }
    }

    private void LogRunResult(PerformanceLabAutomationRunResult result)
    {
        if (result.WasSkipped)
        {
            _activityLog.LogInformation("Performance Lab automation", $"Skipped: {result.SkipReason}");
            return;
        }

        var successes = result.Actions.Count(a => a.Succeeded);
        var failures = result.Actions.Count - successes;
        var message = failures == 0
            ? $"Automation completed ({successes} steps)"
            : $"Automation completed with {failures} failure(s) ({successes} succeeded)";

        var details = result.Actions.Select(a => $"{a.Name}: {(a.Succeeded ? "Success" : "Fail")} - {a.Message}").ToArray();
        _activityLog.LogInformation("Performance Lab automation", message, details);
    }

    private static long ComputeBootMarker()
    {
        try
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var boot = DateTimeOffset.UtcNow - uptime;
            return boot.ToUnixTimeSeconds();
        }
        catch
        {
            return 0;
        }
    }

    private void OnSettingsChanged()
    {
        SettingsChanged?.Invoke(this, _settings);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PerformanceLabAutomationRunner));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runLock.Dispose();
    }
}