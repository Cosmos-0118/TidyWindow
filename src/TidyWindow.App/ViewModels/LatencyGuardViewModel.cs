using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;

namespace TidyWindow.App.ViewModels;

public sealed class LatencyGuardViewModel : ViewModelBase
{
    private const string LogSource = "Latency Guard";

    private readonly ILatencyGuardSampler _sampler;
    private readonly ILatencyGuardProfileService _profileService;
    private readonly ActivityLogService _activityLog;

    public LatencyGuardViewModel(
        ILatencyGuardSampler sampler,
        ILatencyGuardProfileService profileService,
        ActivityLogService activityLogService)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _activityLog = activityLogService ?? throw new ArgumentNullException(nameof(activityLogService));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanExecuteWork);
        ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync, CanExecuteWork);
        RevertProfileCommand = new AsyncRelayCommand(RevertProfileAsync, CanExecuteWork);
        ToggleHagsCommand = new RelayCommand(ToggleHags);
        ApplyOllamaConfigCommand = new RelayCommand(ApplyOllamaConfig);

        _ = InitializeAsync();
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand ApplyProfileCommand { get; }

    public IAsyncRelayCommand RevertProfileCommand { get; }

    public IRelayCommand ToggleHagsCommand { get; }

    public IRelayCommand ApplyOllamaConfigCommand { get; }

    public string HeroHeadline => "Protect audio during heavy GPU workloads";

    public string HeroBody => "Keep Ollama and other AI jobs running while reducing audio crackle by lowering GPU contention and applying low-latency tweaks.";

    public ObservableCollection<string> QuickMitigations { get; } = new()
    {
        "Cap model concurrency or context to keep GPU load under ~70–80%.",
        "Turn off Hardware-Accelerated GPU Scheduling (HAGS) on systems that crackle.",
        "Prefer Studio/WHQL drivers and keep desktop effects light during long runs.",
        "Raise audio stack priority; keep heavy jobs at normal/below-normal CPU priority.",
        "If dual-GPU, pin inference to dGPU and leave iGPU for desktop/audio." 
    };

    public ObservableCollection<string> MonitoringSteps { get; } = new()
    {
        "Watch DPC latency and GPU load; alert when both are high.",
        "List top GPU consumers so users can pause or slow them.",
        "Offer a low-latency profile toggle that trims effects and refresh rate.",
        "Keep Ollama running—prefer throttling over killing jobs." 
    };

    public string StatusNote => "Live sampling is built-in; apply the low-latency profile to trim effects and pause it anytime with Revert.";

    private bool _isBusy;
    public bool IsBusy => _isBusy;

    private bool _trimEffects = true;
    public bool TrimEffects
    {
        get => _trimEffects;
        set => SetProperty(ref _trimEffects, value);
    }

    private bool _trimRefreshRate = true;
    public bool TrimRefreshRate
    {
        get => _trimRefreshRate;
        set => SetProperty(ref _trimRefreshRate, value);
    }

    private bool _throttleModels;
    public bool ThrottleModels
    {
        get => _throttleModels;
        set => SetProperty(ref _throttleModels, value);
    }

    private bool _isProfileActive;
    public bool IsProfileActive
    {
        get => _isProfileActive;
        private set => SetProperty(ref _isProfileActive, value);
    }

    private string _profileStatus = "Profile not applied.";
    public string ProfileStatus
    {
        get => _profileStatus;
        private set => SetProperty(ref _profileStatus, value);
    }

    private double _gpuUtil;
    public double GpuUtil
    {
        get => _gpuUtil;
        private set => SetProperty(ref _gpuUtil, value);
    }

    private double _dpcUtil;
    public double DpcUtil
    {
        get => _dpcUtil;
        private set => SetProperty(ref _dpcUtil, value);
    }

    public ObservableCollection<LatencyGuardSampler.GpuProcessSample> TopProcesses { get; } = new();

    // ─────────────────────────────────────────────────────────────────────────────
    // HAGS (Hardware-Accelerated GPU Scheduling)
    // ─────────────────────────────────────────────────────────────────────────────

    private bool? _isHagsEnabled;
    public bool? IsHagsEnabled
    {
        get => _isHagsEnabled;
        private set => SetProperty(ref _isHagsEnabled, value);
    }

    private string _hagsStatus = "Checking...";
    public string HagsStatus
    {
        get => _hagsStatus;
        private set => SetProperty(ref _hagsStatus, value);
    }

    private void ToggleHags()
    {
        try
        {
            var current = _profileService.IsHagsEnabled();
            var newValue = !(current ?? true);

            _profileService.SetHags(newValue);
            RefreshHagsStatus();
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to toggle HAGS: {ex.Message}", new[] { ex.ToString() });
        }
    }

    private void RefreshHagsStatus()
    {
        var enabled = _profileService.IsHagsEnabled();
        IsHagsEnabled = enabled;

        HagsStatus = enabled switch
        {
            true => "HAGS is ON – may cause audio glitches under heavy GPU load. Toggle off and reboot to fix.",
            false => "HAGS is OFF – better for audio stability. Reboot required if changed.",
            _ => "HAGS status unknown (registry not set)."
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Ollama configuration
    // ─────────────────────────────────────────────────────────────────────────────

    private bool _isOllamaRunning;
    public bool IsOllamaRunning
    {
        get => _isOllamaRunning;
        private set => SetProperty(ref _isOllamaRunning, value);
    }

    private string _ollamaNumCtxText = "2048";
    public string OllamaNumCtxText
    {
        get => _ollamaNumCtxText;
        set => SetProperty(ref _ollamaNumCtxText, value);
    }

    private string _ollamaNumParallelText = "1";
    public string OllamaNumParallelText
    {
        get => _ollamaNumParallelText;
        set => SetProperty(ref _ollamaNumParallelText, value);
    }

    private string _ollamaStatus = "Checking...";
    public string OllamaStatus
    {
        get => _ollamaStatus;
        private set => SetProperty(ref _ollamaStatus, value);
    }

    private void ApplyOllamaConfig()
    {
        try
        {
            int? ctx = int.TryParse(OllamaNumCtxText, out var c) && c > 0 ? c : null;
            int? parallel = int.TryParse(OllamaNumParallelText, out var p) && p > 0 ? p : null;

            _profileService.SetOllamaConfig(ctx, parallel);

            if (_profileService.IsOllamaRunning())
            {
                _profileService.RestartOllama();
                OllamaStatus = $"Ollama restarted with CTX={ctx?.ToString() ?? "default"}, PARALLEL={parallel?.ToString() ?? "default"}.";
            }
            else
            {
                OllamaStatus = $"Config saved. Start Ollama to use CTX={ctx?.ToString() ?? "default"}, PARALLEL={parallel?.ToString() ?? "default"}.";
            }

            RefreshOllamaStatus();
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to apply Ollama configuration: {ex.Message}", new[] { ex.ToString() });
        }
    }

    private void RefreshOllamaStatus()
    {
        IsOllamaRunning = _profileService.IsOllamaRunning();
        var config = _profileService.GetOllamaConfig();

        if (config.NumCtx.HasValue)
        {
            OllamaNumCtxText = config.NumCtx.Value.ToString();
        }
        if (config.NumParallel.HasValue)
        {
            OllamaNumParallelText = config.NumParallel.Value.ToString();
        }

        var ctxPart = config.NumCtx.HasValue ? $"CTX={config.NumCtx}" : "CTX=default";
        var parallelPart = config.NumParallel.HasValue ? $"PARALLEL={config.NumParallel}" : "PARALLEL=default";
        var runningPart = IsOllamaRunning ? "running" : "not running";

        OllamaStatus = $"Ollama {runningPart}. Current: {ctxPart}, {parallelPart}.";
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            _activityLog.LogInformation(LogSource, "Refreshing GPU and DPC metrics.");

            var sample = await _sampler.SampleAsync().ConfigureAwait(false);

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                GpuUtil = sample.GpuUtilPercent;
                DpcUtil = sample.DpcUtilPercent;

                TopProcesses.Clear();
                foreach (var p in sample.TopProcesses)
                {
                    TopProcesses.Add(p);
                }
            });

            var topProcessNames = sample.TopProcesses.Take(3).Select(p => $"{p.Name} ({p.UtilizationPercent:F1}%)");
            _activityLog.LogSuccess(LogSource, $"Metrics refreshed: GPU={sample.GpuUtilPercent:F1}%, DPC={sample.DpcUtilPercent:F1}%.", topProcessNames);
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to refresh metrics: {ex.Message}", new[] { ex.ToString() });
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyProfileAsync()
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            var state = await _profileService.ApplyAsync(TrimEffects, TrimRefreshRate, ThrottleModels).ConfigureAwait(false);
            await UpdateProfileStateAsync(state).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to apply low-latency profile: {ex.Message}", new[] { ex.ToString() });
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RevertProfileAsync()
    {
        if (IsBusy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            var state = await _profileService.RevertAsync().ConfigureAwait(false);
            await UpdateProfileStateAsync(state).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to revert low-latency profile: {ex.Message}", new[] { ex.ToString() });
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var state = await _profileService.GetStateAsync().ConfigureAwait(false);
            await UpdateProfileStateAsync(state).ConfigureAwait(false);

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                RefreshHagsStatus();
                RefreshOllamaStatus();
            });
        }
        catch (Exception ex)
        {
            _activityLog.LogError(LogSource, $"Failed to initialize Latency Guard: {ex.Message}", new[] { ex.ToString() });
        }
    }

    private Task UpdateProfileStateAsync(LatencyGuardProfileState state)
    {
        return App.Current.Dispatcher.InvokeAsync(() =>
        {
            IsProfileActive = state.IsApplied;

            if (!state.IsApplied)
            {
                ProfileStatus = "Profile not applied.";
                return;
            }

            var parts = new ObservableCollection<string>();
            if (state.EffectsTrimmed)
            {
                parts.Add("effects trimmed");
            }
            if (state.RefreshTrimmed)
            {
                parts.Add("refresh capped");
            }
            if (state.ModelThrottleActive)
            {
                parts.Add("model throttle on");
            }

            var appliedAt = state.AppliedAt?.ToLocalTime();
            var appliedText = appliedAt.HasValue ? $" since {appliedAt.Value:t}" : string.Empty;
            var segment = parts.Count > 0 ? string.Join(", ", parts) : "active";

            ProfileStatus = $"Profile active ({segment}){appliedText}.";
        }).Task;
    }

    private bool CanExecuteWork()
    {
        return !IsBusy;
    }

    private void SetBusy(bool value)
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => SetBusy(value));
            return;
        }

        if (SetProperty(ref _isBusy, value))
        {
            (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (ApplyProfileCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (RevertProfileCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }
}
