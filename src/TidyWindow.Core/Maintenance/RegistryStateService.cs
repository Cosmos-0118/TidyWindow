using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Provides cached registry state information for configured tweaks, using PowerShell automation
/// to collect the current value and compare it to the recommended baseline.
/// </summary>
public sealed class RegistryStateService
{
    private const string DetectionScriptRelativePath = "automation/registry/get-registry-state.ps1";
    private const string DetectionScriptOverrideEnvironmentVariable = "TIDYWINDOW_REGISTRY_STATE_SCRIPT";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly RegistryOptimizerService _registryOptimizerService;
    private readonly ConcurrentDictionary<string, RegistryTweakState> _stateCache;
    private readonly Lazy<string> _detectionScriptPath;

    public RegistryStateService(PowerShellInvoker powerShellInvoker, RegistryOptimizerService registryOptimizerService)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _registryOptimizerService = registryOptimizerService ?? throw new ArgumentNullException(nameof(registryOptimizerService));
        _stateCache = new ConcurrentDictionary<string, RegistryTweakState>(StringComparer.OrdinalIgnoreCase);
        _detectionScriptPath = new Lazy<string>(ResolveDetectionScriptPath, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Retrieves the cached registry state for the specified tweak, refreshing if necessary.
    /// </summary>
    public Task<RegistryTweakState> GetStateAsync(string tweakId, CancellationToken cancellationToken = default)
    {
        return GetStateAsync(tweakId, forceRefresh: false, cancellationToken);
    }

    /// <summary>
    /// Retrieves the registry state, optionally forcing a fresh probe to bypass the session cache.
    /// </summary>
    public async Task<RegistryTweakState> GetStateAsync(string tweakId, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            throw new ArgumentException("Tweak identifier must be provided.", nameof(tweakId));
        }

        if (!forceRefresh && _stateCache.TryGetValue(tweakId, out var cached))
        {
            return cached;
        }

        var state = await EvaluateStateAsync(tweakId, cancellationToken).ConfigureAwait(false);
        _stateCache.AddOrUpdate(tweakId, state, (_, _) => state);
        return state;
    }

    /// <summary>
    /// Clears the cached state for the specified tweak or all tweaks when <paramref name="tweakId"/> is null.
    /// </summary>
    public void Invalidate(string? tweakId = null)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            _stateCache.Clear();
            return;
        }

        _stateCache.TryRemove(tweakId, out _);
    }

    private async Task<RegistryTweakState> EvaluateStateAsync(string tweakId, CancellationToken cancellationToken)
    {
        var definition = _registryOptimizerService.GetTweak(tweakId);
        if (definition.Detection is null || definition.Detection.Values.IsDefaultOrEmpty)
        {
            return new RegistryTweakState(
                TweakId: tweakId,
                HasDetection: false,
                MatchesRecommendation: null,
                Values: ImmutableArray<RegistryValueState>.Empty,
                Errors: ImmutableArray<string>.Empty,
                ObservedAt: DateTimeOffset.UtcNow);
        }

        var values = ImmutableArray.CreateBuilder<RegistryValueState>();
        var aggregatedErrors = ImmutableArray.CreateBuilder<string>();
        bool? matchesRecommendation = null;

        foreach (var valueDefinition in definition.Detection.Values)
        {
            var valueState = await ProbeValueAsync(valueDefinition, cancellationToken).ConfigureAwait(false);
            values.Add(valueState);

            if (valueState.Errors.Length > 0)
            {
                aggregatedErrors.AddRange(valueState.Errors);
            }

            if (valueState.IsRecommended is null || valueState.Errors.Length > 0)
            {
                continue;
            }

            matchesRecommendation = matchesRecommendation is null
                ? valueState.IsRecommended
                : matchesRecommendation.Value && valueState.IsRecommended.Value;
        }

        return new RegistryTweakState(
            TweakId: tweakId,
            HasDetection: true,
            MatchesRecommendation: matchesRecommendation,
            Values: values.ToImmutable(),
            Errors: aggregatedErrors.ToImmutable(),
            ObservedAt: DateTimeOffset.UtcNow);
    }

    private async Task<RegistryValueState> ProbeValueAsync(RegistryValueDetection detection, CancellationToken cancellationToken)
    {
        var scriptPath = _detectionScriptPath.Value;
        var parameters = BuildParameters(detection);

        try
        {
            var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
            var normalizedErrors = NormalizeErrors(result.Errors);
            var payload = ExtractJsonPayload(result.Output);

            if (string.IsNullOrWhiteSpace(payload))
            {
                var errors = EnsureFallbackMessage(AppendError(normalizedErrors, "Registry detection script returned no data."));
                return BuildFailureState(detection, errors);
            }

            RegistryProbeModel? model;
            try
            {
                model = JsonSerializer.Deserialize<RegistryProbeModel>(payload, JsonOptions);
            }
            catch (JsonException ex)
            {
                var errors = EnsureFallbackMessage(AppendError(normalizedErrors, "Failed to parse registry detection payload: " + ex.Message));
                return BuildFailureState(detection, errors);
            }

            if (model is null)
            {
                var errors = EnsureFallbackMessage(AppendError(normalizedErrors, "Registry detection payload was empty."));
                return BuildFailureState(detection, errors);
            }

            var valueState = MapToValueState(detection, model);

            var combinedErrors = normalizedErrors;
            if (!result.IsSuccess)
            {
                combinedErrors = AppendError(combinedErrors, "Registry detection completed with errors.");
            }

            if (!combinedErrors.IsDefaultOrEmpty)
            {
                valueState = valueState with { Errors = combinedErrors };
            }

            return valueState;
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
            RecommendedDisplay: null,
            IsRecommended: null,
            Snapshots: ImmutableArray<RegistryValueSnapshot>.Empty,
            Errors: errors);
    }

    private static RegistryValueState MapToValueState(RegistryValueDetection detection, RegistryProbeModel model)
    {
        var snapshots = model.Values is null
            ? ImmutableArray<RegistryValueSnapshot>.Empty
            : model.Values
                .Select(entry => new RegistryValueSnapshot(entry.Path ?? ComposeRegistryPath(detection.Hive, detection.Key), ConvertJsonValue(entry.Value), entry.Display ?? string.Empty))
                .ToImmutableArray();

        var currentValue = ResolveCurrentValue(model.CurrentValue, snapshots);
        var currentDisplay = EnsureDisplay(ExtractDisplayLines(model.CurrentDisplay), currentValue, snapshots);

        var recommendedValue = model.RecommendedValue.HasValue
            ? ConvertJsonValue(model.RecommendedValue.Value)
            : detection.RecommendedValue;

        var recommendedDisplay = EnsureDisplay(model.RecommendedDisplay, recommendedValue);

        var supportsCustomValue = model.SupportsCustomValue || detection.SupportsCustomValue;

        var isRecommended = DetermineRecommendationState(
            model.IsRecommendedState,
            currentDisplay,
            recommendedDisplay,
            currentValue,
            recommendedValue);

        return new RegistryValueState(
            RegistryPathPattern: model.Path ?? ComposeRegistryPath(detection.Hive, detection.Key),
            ValueName: model.ValueName ?? detection.ValueName,
            LookupValueName: model.LookupValueName ?? detection.LookupValueName,
            ValueType: model.ValueType ?? detection.ValueType,
            SupportsCustomValue: supportsCustomValue,
            CurrentValue: currentValue,
            CurrentDisplay: currentDisplay,
            RecommendedValue: recommendedValue,
            RecommendedDisplay: recommendedDisplay,
            IsRecommended: isRecommended,
            Snapshots: snapshots,
            Errors: ImmutableArray<string>.Empty);
    }

    private static object? ResolveCurrentValue(JsonElement? currentValueElement, ImmutableArray<RegistryValueSnapshot> snapshots)
    {
        if (currentValueElement.HasValue)
        {
            return ConvertJsonValue(currentValueElement.Value);
        }

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Value is not null)
            {
                return snapshot.Value;
            }
        }

        return null;
    }

    private static ImmutableArray<string> EnsureDisplay(ImmutableArray<string> display, object? fallbackValue, ImmutableArray<RegistryValueSnapshot> snapshots)
    {
        if (HasDisplayContent(display))
        {
            return display;
        }

        if (fallbackValue is not null)
        {
            var formattedFallback = FormatRegistryValueForDisplay(fallbackValue);
            if (!string.IsNullOrWhiteSpace(formattedFallback))
            {
                return ImmutableArray.Create(formattedFallback);
            }
        }

        foreach (var snapshot in snapshots)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Display))
            {
                return ImmutableArray.Create(snapshot.Display.Trim());
            }

            if (snapshot.Value is not null)
            {
                var formatted = FormatRegistryValueForDisplay(snapshot.Value);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    return ImmutableArray.Create(formatted);
                }
            }
        }

        return ImmutableArray<string>.Empty;
    }

    private static ImmutableArray<string> EnsureDisplay(ImmutableArray<string> display, object? fallbackValue)
    {
        if (HasDisplayContent(display))
        {
            return display;
        }

        if (fallbackValue is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var formatted = FormatRegistryValueForDisplay(fallbackValue);
        return string.IsNullOrWhiteSpace(formatted)
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(formatted);
    }

    private static string? EnsureDisplay(string? display, object? fallbackValue)
    {
        if (!string.IsNullOrWhiteSpace(display))
        {
            return display;
        }

        var formatted = FormatRegistryValueForDisplay(fallbackValue);
        return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
    }

    private static bool? DetermineRecommendationState(
        bool? scriptState,
        ImmutableArray<string> currentDisplay,
        string? recommendedDisplay,
        object? currentValue,
        object? recommendedValue)
    {
        var computed = ComputeRecommendation(currentDisplay, recommendedDisplay, currentValue, recommendedValue);
        return computed ?? scriptState;
    }

    private static bool? ComputeRecommendation(
        ImmutableArray<string> currentDisplay,
        string? recommendedDisplay,
        object? currentValue,
        object? recommendedValue)
    {
        if (recommendedValue is null && string.IsNullOrWhiteSpace(recommendedDisplay))
        {
            return null;
        }

        if (recommendedValue is not null)
        {
            var structural = AreValuesEquivalent(currentValue, recommendedValue);
            if (structural.HasValue)
            {
                return structural;
            }
        }

        var currentText = GetFirstDisplayLine(currentDisplay);
        if (string.IsNullOrWhiteSpace(currentText))
        {
            currentText = FormatRegistryValueForDisplay(currentValue);
        }

        var expectedText = !string.IsNullOrWhiteSpace(recommendedDisplay)
            ? recommendedDisplay
            : FormatRegistryValueForDisplay(recommendedValue);

        if (string.IsNullOrWhiteSpace(expectedText))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(currentText))
        {
            return recommendedValue is null ? true : false;
        }

        return string.Equals(currentText, expectedText, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetFirstDisplayLine(ImmutableArray<string> display)
    {
        if (display.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var line in display)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return null;
    }

    private static bool? AreValuesEquivalent(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is IEnumerable leftEnumerable && left is not string && right is IEnumerable rightEnumerable && right is not string)
        {
            var leftList = leftEnumerable.Cast<object?>().ToList();
            var rightList = rightEnumerable.Cast<object?>().ToList();

            if (leftList.Count != rightList.Count)
            {
                return false;
            }

            for (var index = 0; index < leftList.Count; index++)
            {
                var nested = AreValuesEquivalent(leftList[index], rightList[index]);
                if (nested != true)
                {
                    return nested;
                }
            }

            return true;
        }

        var leftScalar = NormalizeComparableString(left);
        var rightScalar = NormalizeComparableString(right);

        if (leftScalar is null || rightScalar is null)
        {
            return null;
        }

        return string.Equals(leftScalar, rightScalar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeComparableString(object? value)
    {
        return value switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim(),
            bool b => b ? "True" : "False",
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()?.Trim()
        };
    }

    private static string? FormatRegistryValueForDisplay(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return s;
        }

        if (value is bool b)
        {
            return b ? "True" : "False";
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var parts = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    parts.Add("<null>");
                    continue;
                }

                var formatted = FormatRegistryValueForDisplay(item);
                parts.Add(string.IsNullOrWhiteSpace(formatted) ? item.ToString() ?? string.Empty : formatted);
            }

            return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        }

        return Convert.ToString(value, CultureInfo.CurrentCulture);
    }

    private static bool HasDisplayContent(ImmutableArray<string> display)
    {
        if (display.IsDefaultOrEmpty)
        {
            return false;
        }

        foreach (var line in display)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return true;
            }
        }

        return false;
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
            parameters["RecommendedValue"] = SerializeRecommendedValue(detection.RecommendedValue);
        }

        if (!string.IsNullOrWhiteSpace(detection.LookupValueName))
        {
            parameters["LookupValueName"] = detection.LookupValueName;
        }

        return parameters;
    }

    private static ImmutableArray<string> ExtractDisplayLines(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ImmutableArray<string>.Empty;
        }

        var value = element.Value;
        if (value.ValueKind == JsonValueKind.Array)
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var entry in value.EnumerateArray())
            {
                var text = FormatDisplayElement(entry);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.Add(text);
                }
            }

            return builder.Count == 0 ? ImmutableArray<string>.Empty : builder.ToImmutable();
        }

        var single = FormatDisplayElement(value);
        return string.IsNullOrWhiteSpace(single)
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(single);
    }

    private static string FormatDisplayElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => element.ToString()
        } ?? string.Empty;
    }

    private static string ComposeRegistryPath(string hive, string key)
    {
        var trimmedKey = (key ?? string.Empty).TrimStart('\\');
        return string.IsNullOrWhiteSpace(trimmedKey)
            ? $"{hive}:\\"
            : $"{hive}:\\{trimmedKey}";
    }

    private static string SerializeRecommendedValue(object value)
    {
        return value switch
        {
            string s => s,
            bool b => b ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable enumerable when value is not string => string.Join(',', enumerable.Cast<object?>().Where(static item => item is not null).Select(static item => Convert.ToString(item, CultureInfo.InvariantCulture))),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string? ExtractJsonPayload(IReadOnlyList<string> output)
    {
        if (output is null || output.Count == 0)
        {
            return null;
        }

        for (var index = output.Count - 1; index >= 0; index--)
        {
            var candidate = output[index];
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var trimmedCandidate = candidate.TrimStart('\uFEFF');
            var leadingTrimmed = trimmedCandidate.TrimStart();
            if (!StartsJsonEnvelope(leadingTrimmed))
            {
                continue;
            }

            var normalizedCandidate = trimmedCandidate.Trim();
            if (TryNormalizeJson(normalizedCandidate, out var payload))
            {
                return payload;
            }

            var builder = new StringBuilder();
            for (var cursor = index; cursor < output.Count; cursor++)
            {
                var segment = output[cursor];
                if (segment is null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(segment.TrimStart('\uFEFF'));

                var combined = builder.ToString().Trim();
                if (!StartsJsonEnvelope(combined))
                {
                    continue;
                }

                if (TryNormalizeJson(combined, out payload))
                {
                    return payload;
                }
            }
        }

        return null;
    }

    private static bool StartsJsonEnvelope(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var first = value[0];
        return first == '{' || first == '[';
    }

    private static bool TryNormalizeJson(string value, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = string.Empty;
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            normalized = value.Trim();
            return true;
        }
        catch (JsonException)
        {
            normalized = string.Empty;
            return false;
        }
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

            // Strip common ANSI escape sequences that may appear when scripts log colourized messages.
            var trimmed = System.Text.RegularExpressions.Regex.Replace(entry, @"\x1B\[[0-9;?]*[ -/]*[@-~]", string.Empty);
            trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\x1B", string.Empty);
            trimmed = trimmed.Trim();
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

    private static ImmutableArray<string> AppendError(ImmutableArray<string> errors, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return errors.IsDefault ? ImmutableArray<string>.Empty : errors;
        }

        var trimmed = message.Trim();
        if (errors.IsDefaultOrEmpty)
        {
            return ImmutableArray.Create(trimmed);
        }

        if (errors.Any(line => string.Equals(line, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return errors;
        }

        var builder = errors.ToBuilder();
        builder.Add(trimmed);
        return builder.ToImmutable();
    }

    private static ImmutableArray<string> EnsureFallbackMessage(ImmutableArray<string> errors)
    {
        return errors.IsDefaultOrEmpty
            ? ImmutableArray.Create("Registry detection failed without diagnostics.")
            : errors;
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

        public JsonElement Value { get; set; }

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