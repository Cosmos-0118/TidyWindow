using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Simplifies how registry current values are surfaced by reading the JSON output emitted by the detection script.
/// </summary>
public sealed class RegistryStateService : IRegistryStateService
{
    private const string DetectionScriptRelativePath = "automation/registry/get-registry-state.ps1";
    private const string DetectionScriptOverrideEnvironmentVariable = "TIDYWINDOW_REGISTRY_STATE_SCRIPT";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Lazy<string?> OriginalUserSid = new(ResolveOriginalUserSid, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly IRegistryOptimizerService _registryOptimizerService;
    private readonly Lazy<string> _detectionScriptPath;
    private readonly Lazy<string> _cacheDirectory;

    public RegistryStateService(PowerShellInvoker powerShellInvoker, IRegistryOptimizerService registryOptimizerService)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _registryOptimizerService = registryOptimizerService ?? throw new ArgumentNullException(nameof(registryOptimizerService));
        _detectionScriptPath = new Lazy<string>(ResolveDetectionScriptPath, LazyThreadSafetyMode.ExecutionAndPublication);
        _cacheDirectory = new Lazy<string>(() => ResolveCacheDirectory(), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<RegistryTweakState> GetStateAsync(string tweakId, CancellationToken cancellationToken = default)
    {
        return GetStateAsync(tweakId, forceRefresh: false, cancellationToken);
    }

    public Task<RegistryTweakState> GetStateAsync(string tweakId, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            throw new ArgumentException("Tweak identifier must be provided.", nameof(tweakId));
        }

        _ = forceRefresh; // no cache retained, always probe fresh
        return EvaluateStateAsync(tweakId, cancellationToken);
    }

    public void Invalidate(string? tweakId = null)
    {
        _ = tweakId; // retained for interface compatibility, nothing to clear
    }

    private async Task<RegistryTweakState> EvaluateStateAsync(string tweakId, CancellationToken cancellationToken)
    {
        var definition = _registryOptimizerService.GetTweak(tweakId);
        if (definition.Detection is null || definition.Detection.Values.IsDefaultOrEmpty)
        {
            return new RegistryTweakState(tweakId, false, null, ImmutableArray<RegistryValueState>.Empty, ImmutableArray<string>.Empty, DateTimeOffset.UtcNow);
        }

        var values = ImmutableArray.CreateBuilder<RegistryValueState>();
        var errors = ImmutableArray.CreateBuilder<string>();

        for (var index = 0; index < definition.Detection.Values.Length; index++)
        {
            var valueDefinition = definition.Detection.Values[index];
            var state = await ProbeValueAsync(tweakId, valueDefinition, index, cancellationToken).ConfigureAwait(false);
            values.Add(state);

            if (!state.Errors.IsDefaultOrEmpty)
            {
                errors.AddRange(state.Errors);
            }
        }

        bool? matchesRecommendation = values
            .Select(v => v.IsRecommended)
            .Where(flag => flag.HasValue)
            .Aggregate((bool?)null, static (current, flag) => current is null ? flag : current.Value && flag!.Value);

        var observedAt = DateTimeOffset.UtcNow;
        var stateResult = new RegistryTweakState(tweakId, true, matchesRecommendation, values.ToImmutable(), errors.ToImmutable(), observedAt);
        await WriteAggregatedStateAsync(stateResult, cancellationToken).ConfigureAwait(false);
        return stateResult;
    }

    private async Task<RegistryValueState> ProbeValueAsync(string tweakId, RegistryValueDetection detection, int index, CancellationToken cancellationToken)
    {
        var parameters = BuildParameters(detection);

        try
        {
            var scriptPath = _detectionScriptPath.Value;
            var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);

            var model = ParseProbeModel(result.Output);
            var scriptErrors = NormalizeErrors(result.Errors);

            if (model is null)
            {
                var errors = scriptErrors.IsDefaultOrEmpty
                    ? ImmutableArray.Create("Registry detection script did not produce JSON output.")
                    : scriptErrors;

                return BuildFailureState(detection, errors);
            }

            await WriteProbeModelAsync(tweakId, detection, index, model, cancellationToken).ConfigureAwait(false);

            var mapped = MapToValueState(detection, model);

            if (!scriptErrors.IsDefaultOrEmpty)
            {
                mapped = mapped with { Errors = scriptErrors };
            }

            if (!result.IsSuccess && scriptErrors.IsDefaultOrEmpty)
            {
                mapped = mapped with { Errors = ImmutableArray.Create("Registry detection script completed with errors.") };
            }

            return mapped;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildFailureState(detection, ImmutableArray.Create(ex.Message));
        }
    }

    private static RegistryProbeModel? ParseProbeModel(IReadOnlyList<string> rawOutput)
    {
        if (rawOutput is null || rawOutput.Count == 0)
        {
            return null;
        }

        for (var index = rawOutput.Count - 1; index >= 0; index--)
        {
            var line = rawOutput[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.TrimStart('\uFEFF').TrimStart();
            if (!LooksLikeJsonPayload(trimmed))
            {
                continue;
            }

            if (TryDeserialize(trimmed, out var directModel))
            {
                return directModel;
            }

            var builder = new System.Text.StringBuilder();
            for (var cursor = index; cursor < rawOutput.Count; cursor++)
            {
                var segment = rawOutput[cursor];
                if (segment is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(segment.TrimStart('\uFEFF'));
                var payload = builder.ToString().Trim();

                if (!LooksLikeJsonPayload(payload))
                {
                    continue;
                }

                if (TryDeserialize(payload, out var model))
                {
                    return model;
                }
            }
        }

        return null;
    }

    private static bool LooksLikeJsonPayload(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            return ch == '{' || ch == '[';
        }

        return false;
    }

    private static bool TryDeserialize(string payload, out RegistryProbeModel? model)
    {
        try
        {
            model = JsonSerializer.Deserialize<RegistryProbeModel>(payload, JsonOptions);
            return model is not null;
        }
        catch
        {
            model = null;
            return false;
        }
    }

    private static RegistryValueState MapToValueState(RegistryValueDetection detection, RegistryProbeModel model)
    {
        var snapshots = model.Values is null
            ? ImmutableArray<RegistryValueSnapshot>.Empty
            : model.Values.Select(entry => new RegistryValueSnapshot(
                    entry.Path ?? ComposeRegistryPath(detection.Hive, detection.Key),
                    entry.Value is null ? null : ConvertJsonValue(entry.Value.Value),
                    entry.Display ?? string.Empty))
                .ToImmutableArray();

        var currentDisplay = ToDisplayList(model.CurrentDisplay);
        var currentValue = model.CurrentValue is null ? null : ConvertJsonValue(model.CurrentValue.Value);

        var recommendedValue = model.RecommendedValue is null
            ? detection.RecommendedValue
            : ConvertJsonValue(model.RecommendedValue.Value);

        var recommendedDisplay = !string.IsNullOrWhiteSpace(model.RecommendedDisplay)
            ? model.RecommendedDisplay
            : FormatValue(recommendedValue);

        return new RegistryValueState(
            RegistryPathPattern: model.Path ?? ComposeRegistryPath(detection.Hive, detection.Key),
            ValueName: model.ValueName ?? detection.ValueName,
            LookupValueName: model.LookupValueName ?? detection.LookupValueName,
            ValueType: model.ValueType ?? detection.ValueType,
            SupportsCustomValue: model.SupportsCustomValue || detection.SupportsCustomValue,
            CurrentValue: currentValue,
            CurrentDisplay: currentDisplay,
            RecommendedValue: recommendedValue,
            RecommendedDisplay: recommendedDisplay,
            IsRecommended: model.IsRecommendedState,
            Snapshots: snapshots,
            Errors: ImmutableArray<string>.Empty);
    }

    private static ImmutableArray<string> ToDisplayList(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ImmutableArray<string>.Empty;
        }

        if (element.Value.ValueKind == JsonValueKind.Array)
        {
            return element.Value
                .EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToImmutableArray();
        }

        var single = element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : element.Value.ToString();
        return string.IsNullOrWhiteSpace(single)
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(single!.Trim());
    }

    private static RegistryValueState BuildFailureState(RegistryValueDetection detection, ImmutableArray<string> errors)
    {
        return new RegistryValueState(
            RegistryPathPattern: ComposeRegistryPath(detection.Hive, detection.Key),
            ValueName: detection.ValueName,
            LookupValueName: detection.LookupValueName,
            ValueType: detection.ValueType,
            SupportsCustomValue: detection.SupportsCustomValue,
            CurrentValue: null,
            CurrentDisplay: ImmutableArray<string>.Empty,
            RecommendedValue: detection.RecommendedValue,
            RecommendedDisplay: FormatValue(detection.RecommendedValue),
            IsRecommended: null,
            Snapshots: ImmutableArray<RegistryValueSnapshot>.Empty,
            Errors: errors);
    }

    private static ImmutableArray<string> NormalizeErrors(IReadOnlyList<string>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var entry in errors)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                builder.Add(trimmed);
            }
        }

        return builder.ToImmutable();
    }

    private static Dictionary<string, object?> BuildParameters(RegistryValueDetection detection)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegistryPath"] = ComposeRegistryPath(detection.Hive, detection.Key),
            ["ValueName"] = detection.ValueName,
            ["ValueType"] = detection.ValueType,
            ["SupportsCustomValue"] = detection.SupportsCustomValue
        };

        if (detection.RecommendedValue is not null)
        {
            parameters["RecommendedValue"] = FormatValue(detection.RecommendedValue);
        }

        if (!string.IsNullOrWhiteSpace(detection.LookupValueName))
        {
            parameters["LookupValueName"] = detection.LookupValueName;
        }

        if (string.Equals(detection.Hive, "HKCU", StringComparison.OrdinalIgnoreCase))
        {
            var sid = OriginalUserSid.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                parameters["UserSid"] = sid;
            }
        }

        return parameters;
    }

    private static string SanitizePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "value";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
            {
                builder.Append('_');
            }
            else if (char.IsWhiteSpace(ch))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var sanitized = builder.ToString().Trim('_');
        return sanitized.Length == 0 ? "value" : sanitized;
    }

    private async Task WriteProbeModelAsync(string tweakId, RegistryValueDetection detection, int index, RegistryProbeModel model, CancellationToken cancellationToken)
    {
        try
        {
            var tweakDirectory = Path.Combine(_cacheDirectory.Value, SanitizePathSegment(tweakId));
            Directory.CreateDirectory(tweakDirectory);

            var baseName = SanitizePathSegment(model.ValueName ?? detection.ValueName ?? $"value-{index}");
            var fileName = $"{baseName}-{index}.json";
            var destination = Path.Combine(tweakDirectory, fileName);

            await using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, model, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cache persistence is best-effort.
        }
    }

    private async Task WriteAggregatedStateAsync(RegistryTweakState state, CancellationToken cancellationToken)
    {
        try
        {
            var tweakDirectory = Path.Combine(_cacheDirectory.Value, SanitizePathSegment(state.TweakId));
            Directory.CreateDirectory(tweakDirectory);

            var model = new RegistryStateCacheModel
            {
                TweakId = state.TweakId,
                ObservedAt = state.ObservedAt,
                MatchesRecommendation = state.MatchesRecommendation,
                Errors = state.Errors.Where(static e => !string.IsNullOrWhiteSpace(e)).Select(static e => e.Trim()).ToList(),
                Values = state.Values.Select(static v => new RegistryValueCacheModel
                {
                    RegistryPathPattern = v.RegistryPathPattern,
                    ValueName = v.ValueName,
                    LookupValueName = v.LookupValueName,
                    ValueType = v.ValueType,
                    SupportsCustomValue = v.SupportsCustomValue,
                    CurrentValue = v.CurrentValue,
                    CurrentDisplay = v.CurrentDisplay.IsDefaultOrEmpty ? new List<string>() : v.CurrentDisplay.Where(static line => !string.IsNullOrWhiteSpace(line)).Select(static line => line.Trim()).ToList(),
                    RecommendedValue = v.RecommendedValue,
                    RecommendedDisplay = v.RecommendedDisplay,
                    IsRecommended = v.IsRecommended,
                    Snapshots = v.Snapshots.Select(static snapshot => new RegistrySnapshotCacheModel
                    {
                        Path = snapshot.Path,
                        Value = snapshot.Value,
                        Display = snapshot.Display
                    }).ToList(),
                    Errors = v.Errors.IsDefaultOrEmpty ? new List<string>() : v.Errors.Where(static e => !string.IsNullOrWhiteSpace(e)).Select(static e => e.Trim()).ToList()
                }).ToList()
            };

            var destination = Path.Combine(tweakDirectory, "current-values.json");
            await using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, model, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cache hydration is best-effort.
        }
    }

    private string ResolveCacheDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var directory = new DirectoryInfo(baseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "data", "cache", "registry");
            if (Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }

            directory = directory.Parent;
        }

        var fallback = Path.Combine(baseDirectory, "data", "cache", "registry");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string ComposeRegistryPath(string hive, string key)
    {
        var trimmedKey = (key ?? string.Empty).TrimStart('\\');
        return string.IsNullOrWhiteSpace(trimmedKey)
            ? $"{hive}:\\"
            : $"{hive}:\\{trimmedKey}";
    }

    private static string? FormatValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            bool b => b ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString()
        };
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDouble(out var doubleValue)
                    ? doubleValue
                    : element.ToString(),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonValue(p.Value), StringComparer.OrdinalIgnoreCase),
            _ => element.ToString()
        };
    }

    private static string ResolveDetectionScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(DetectionScriptOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, DetectionScriptRelativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, DetectionScriptRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate registry detection script at '{DetectionScriptRelativePath}'.", DetectionScriptRelativePath);
    }

    private static string? ResolveOriginalUserSid()
    {
        var sid = Environment.GetEnvironmentVariable(RegistryUserContext.OriginalUserSidEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(sid))
        {
            return sid;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity?.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private sealed class RegistryStateCacheModel
    {
        public string TweakId { get; set; } = string.Empty;

        public DateTimeOffset ObservedAt { get; set; }

        public bool? MatchesRecommendation { get; set; }

        public List<string> Errors { get; set; } = new();

        public List<RegistryValueCacheModel> Values { get; set; } = new();
    }

    private sealed class RegistryValueCacheModel
    {
        public string RegistryPathPattern { get; set; } = string.Empty;

        public string ValueName { get; set; } = string.Empty;

        public string? LookupValueName { get; set; }

        public string ValueType { get; set; } = string.Empty;

        public bool SupportsCustomValue { get; set; }

        public object? CurrentValue { get; set; }

        public List<string> CurrentDisplay { get; set; } = new();

        public object? RecommendedValue { get; set; }

        public string? RecommendedDisplay { get; set; }

        public bool? IsRecommended { get; set; }

        public List<RegistrySnapshotCacheModel> Snapshots { get; set; } = new();

        public List<string> Errors { get; set; } = new();
    }

    private sealed class RegistrySnapshotCacheModel
    {
        public string Path { get; set; } = string.Empty;

        public object? Value { get; set; }

        public string Display { get; set; } = string.Empty;
    }

    private sealed class RegistryProbeModel
    {
        public string? Path { get; set; }

        public string? ValueName { get; set; }

        public string? LookupValueName { get; set; }

        public string? ValueType { get; set; }

        public bool SupportsCustomValue { get; set; }

        public JsonElement? CurrentValue { get; set; }

        public JsonElement? CurrentDisplay { get; set; }

        public JsonElement? RecommendedValue { get; set; }

        public string? RecommendedDisplay { get; set; }

        public bool? IsRecommendedState { get; set; }

        public List<RegistryProbeEntry>? Values { get; set; }
    }

    private sealed class RegistryProbeEntry
    {
        public string? Path { get; set; }

        public JsonElement? Value { get; set; }

        public string? Display { get; set; }
    }
}

public sealed record RegistryTweakState(
    string TweakId,
    bool HasDetection,
    bool? MatchesRecommendation,
    ImmutableArray<RegistryValueState> Values,
    ImmutableArray<string> Errors,
    DateTimeOffset ObservedAt);

public sealed record RegistryValueState(
    string RegistryPathPattern,
    string ValueName,
    string? LookupValueName,
    string ValueType,
    bool SupportsCustomValue,
    object? CurrentValue,
    ImmutableArray<string> CurrentDisplay,
    object? RecommendedValue,
    string? RecommendedDisplay,
    bool? IsRecommended,
    ImmutableArray<RegistryValueSnapshot> Snapshots,
    ImmutableArray<string> Errors);

public sealed record RegistryValueSnapshot(string Path, object? Value, string Display);
