using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Provides presets, metadata, and execution helpers for registry optimizer tweaks.
/// </summary>
public sealed class RegistryOptimizerService
{
    private const string ConfigurationRelativePath = "data/cleanup/registry-defaults.json";
    private const int MaxRestorePoints = 10;

    private static readonly string RestorePointRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TidyWindow",
        "RegistryBackups");

    private static readonly JsonSerializerOptions RestorePointSerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly Lazy<RegistryOptimizerConfiguration> _configuration;

    public RegistryOptimizerService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _configuration = new Lazy<RegistryOptimizerConfiguration>(LoadConfiguration, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the available tweak definitions.
    /// </summary>
    public IReadOnlyList<RegistryTweakDefinition> Tweaks => _configuration.Value.Tweaks;

    /// <summary>
    /// Gets the published presets.
    /// </summary>
    public IReadOnlyList<RegistryPresetDefinition> Presets => _configuration.Value.Presets;

    /// <summary>
    /// Resolves a tweak definition by identifier.
    /// </summary>
    public RegistryTweakDefinition GetTweak(string tweakId)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            throw new ArgumentException("Tweak id must be provided.", nameof(tweakId));
        }

        var match = _configuration.Value.TweakLookup.GetValueOrDefault(tweakId);
        return match ?? throw new InvalidOperationException($"Unknown registry tweak '{tweakId}'.");
    }

    /// <summary>
    /// Builds the plan of script invocations required to reach the requested selection states along with the revert payload.
    /// </summary>
    public RegistryOperationPlan BuildPlan(IEnumerable<RegistrySelection> selections)
    {
        if (selections is null)
        {
            throw new ArgumentNullException(nameof(selections));
        }

        var apply = ImmutableArray.CreateBuilder<RegistryScriptInvocation>();
        var revert = ImmutableArray.CreateBuilder<RegistryScriptInvocation>();

        foreach (var selection in selections)
        {
            if (string.IsNullOrWhiteSpace(selection.TweakId))
            {
                throw new ArgumentException("Selection must specify a tweak id.", nameof(selections));
            }

            var definition = GetTweak(selection.TweakId);

            var targetOperation = definition.ResolveOperation(selection.TargetState);
            if (targetOperation is not null)
            {
                apply.Add(CreateInvocation(definition, targetOperation, selection.TargetState, selection.TargetParameters));
            }

            var revertOperation = definition.ResolveOperation(selection.PreviousState);
            if (revertOperation is not null)
            {
                revert.Add(CreateInvocation(definition, revertOperation, selection.PreviousState, selection.PreviousParameters));
            }
        }

        return new RegistryOperationPlan(apply.ToImmutable(), revert.ToImmutable());
    }

    /// <summary>
    /// Executes the supplied plan sequentially, returning the collected outputs and errors per step.
    /// </summary>
    public Task<RegistryOperationResult> ApplyAsync(RegistryOperationPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        return ExecuteOperationsAsync(plan.ApplyOperations, cancellationToken);
    }

    public async Task<RegistryRestorePoint?> SaveRestorePointAsync(IEnumerable<RegistrySelection> selections, RegistryOperationPlan plan, CancellationToken cancellationToken = default)
    {
        if (selections is null)
        {
            throw new ArgumentNullException(nameof(selections));
        }

        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (plan.RevertOperations.Length == 0)
        {
            return null;
        }

        var selectionSnapshots = selections.ToImmutableArray();
        if (selectionSnapshots.Length == 0)
        {
            return null;
        }

        var restoreSelections = selectionSnapshots
            .Select(selection => new RegistryRestoreSelection(selection.TweakId, selection.PreviousState, selection.TargetState))
            .ToImmutableArray();

        var restoreOperations = plan.RevertOperations
            .Select(ToRestoreOperation)
            .ToImmutableArray();

        if (restoreOperations.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(RestorePointRoot);

        var timestamp = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var fileName = $"{timestamp:yyyyMMdd-HHmmssfff}-{id:N}.json";
        var filePath = Path.Combine(RestorePointRoot, fileName);

        var model = new RegistryRestorePointModel
        {
            Id = id,
            CreatedUtc = timestamp,
            Selections = restoreSelections.Select(selection => new RegistryRestoreSelectionModel
            {
                TweakId = selection.TweakId,
                PreviousState = selection.PreviousState,
                TargetState = selection.TargetState
            }).ToArray(),
            Operations = restoreOperations.Select(operation => new RegistryRestoreOperationModel
            {
                TweakId = operation.TweakId,
                Name = operation.Name,
                TargetState = operation.TargetState,
                ScriptPath = operation.ScriptPath,
                Parameters = operation.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            }).ToArray()
        };

        await using (var stream = File.Create(filePath))
        {
            await JsonSerializer.SerializeAsync(stream, model, RestorePointSerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        PruneRestorePoints();

        return new RegistryRestorePoint(id, filePath, timestamp, restoreSelections, restoreOperations);
    }

    public RegistryRestorePoint? TryGetLatestRestorePoint()
    {
        try
        {
            if (!Directory.Exists(RestorePointRoot))
            {
                return null;
            }

            var directory = new DirectoryInfo(RestorePointRoot);
            var files = directory.GetFiles("*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var restorePoint = LoadRestorePoint(file.FullName);
                if (restorePoint is not null)
                {
                    return restorePoint;
                }
            }
        }
        catch
        {
            // Ignore discovery failures and surface no restore point.
        }

        return null;
    }

    public Task<RegistryOperationResult> ApplyRestorePointAsync(RegistryRestorePoint restorePoint, CancellationToken cancellationToken = default)
    {
        if (restorePoint is null)
        {
            throw new ArgumentNullException(nameof(restorePoint));
        }

        var operations = restorePoint.Operations
            .Select(CreateInvocation)
            .ToImmutableArray();

        return ExecuteOperationsAsync(operations, cancellationToken);
    }

    public void DeleteRestorePoint(RegistryRestorePoint restorePoint)
    {
        if (restorePoint is null)
        {
            throw new ArgumentNullException(nameof(restorePoint));
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(restorePoint.FilePath) && File.Exists(restorePoint.FilePath))
            {
                File.Delete(restorePoint.FilePath);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private async Task<RegistryOperationResult> ExecuteOperationsAsync(ImmutableArray<RegistryScriptInvocation> operations, CancellationToken cancellationToken)
    {
        var executions = ImmutableArray.CreateBuilder<RegistryExecutionSummary>();

        foreach (var invocation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await _powerShellInvoker.InvokeScriptAsync(invocation.ScriptPath, invocation.Parameters, cancellationToken).ConfigureAwait(false);
                executions.Add(new RegistryExecutionSummary(invocation, result.Output.ToImmutableArray(), result.Errors.ToImmutableArray(), result.ExitCode));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                executions.Add(new RegistryExecutionSummary(invocation, ImmutableArray<string>.Empty, ImmutableArray.Create(ex.Message), -1));
            }
        }

        return new RegistryOperationResult(executions.ToImmutable());
    }

    private static RegistryRestoreOperation ToRestoreOperation(RegistryScriptInvocation invocation)
    {
        var parameters = invocation.Parameters is { Count: > 0 }
            ? invocation.Parameters.ToDictionary(pair => pair.Key, pair => SerializeParameter(pair.Value), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        return new RegistryRestoreOperation(
            invocation.TweakId,
            invocation.Name,
            invocation.TargetState,
            invocation.ScriptPath,
            parameters.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private RegistryScriptInvocation CreateInvocation(RegistryRestoreOperation operation)
    {
        var parameters = BuildParameterDictionary(operation.Parameters);
        return new RegistryScriptInvocation(operation.TweakId, operation.Name, operation.TargetState, operation.ScriptPath, parameters);
    }

    private static IReadOnlyDictionary<string, object?> BuildParameterDictionary(IReadOnlyDictionary<string, string?> parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parameters)
        {
            builder[pair.Key] = DeserializeParameter(pair.Value);
        }

        return builder.ToImmutable();
    }

    private static string? SerializeParameter(object? value)
    {
        return value is null ? null : JsonSerializer.Serialize(value, RestorePointSerializerOptions);
    }

    private static object? DeserializeParameter(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return ConvertJsonValue(document.RootElement);
    }

    private static RegistryRestorePoint? LoadRestorePoint(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var model = JsonSerializer.Deserialize<RegistryRestorePointModel>(stream, RestorePointSerializerOptions);
            if (model is null)
            {
                return null;
            }

            var selections = model.Selections is { Length: > 0 }
                ? model.Selections
                    .Where(selection => !string.IsNullOrWhiteSpace(selection.TweakId))
                    .Select(selection => new RegistryRestoreSelection(selection.TweakId!, selection.PreviousState, selection.TargetState))
                    .ToImmutableArray()
                : ImmutableArray<RegistryRestoreSelection>.Empty;

            var operations = model.Operations is { Length: > 0 }
                ? model.Operations
                    .Where(operation => !string.IsNullOrWhiteSpace(operation.TweakId) && !string.IsNullOrWhiteSpace(operation.ScriptPath))
                    .Select(operation => new RegistryRestoreOperation(
                        operation.TweakId!,
                        string.IsNullOrWhiteSpace(operation.Name) ? operation.TweakId! : operation.Name!,
                        operation.TargetState,
                        operation.ScriptPath!,
                        (operation.Parameters ?? new Dictionary<string, string?>()).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)))
                    .ToImmutableArray()
                : ImmutableArray<RegistryRestoreOperation>.Empty;

            if (operations.Length == 0)
            {
                return null;
            }

            var createdUtc = model.CreatedUtc != default ? model.CreatedUtc : File.GetLastWriteTimeUtc(filePath);
            var id = model.Id != Guid.Empty ? model.Id : ExtractIdFromFileName(filePath) ?? Guid.NewGuid();

            return new RegistryRestorePoint(id, filePath, createdUtc, selections, operations);
        }
        catch
        {
            return null;
        }
    }

    private static Guid? ExtractIdFromFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var candidate = parts.LastOrDefault();
        return Guid.TryParse(candidate, out var id) ? id : null;
    }

    private static void PruneRestorePoints()
    {
        try
        {
            if (!Directory.Exists(RestorePointRoot))
            {
                return;
            }

            var directory = new DirectoryInfo(RestorePointRoot);
            var files = directory.GetFiles("*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var index = MaxRestorePoints; index < files.Count; index++)
            {
                try
                {
                    files[index].Delete();
                }
                catch
                {
                    // Ignore pruning failures.
                }
            }
        }
        catch
        {
            // Ignore pruning failures.
        }
    }

    private sealed class RegistryRestorePointModel
    {
        public Guid Id { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public RegistryRestoreSelectionModel[]? Selections { get; set; }

        public RegistryRestoreOperationModel[]? Operations { get; set; }
    }

    private sealed class RegistryRestoreSelectionModel
    {
        public string? TweakId { get; set; }

        public bool PreviousState { get; set; }

        public bool TargetState { get; set; }
    }

    private sealed class RegistryRestoreOperationModel
    {
        public string? TweakId { get; set; }

        public string? Name { get; set; }

        public bool TargetState { get; set; }

        public string? ScriptPath { get; set; }

        public Dictionary<string, string?>? Parameters { get; set; }
    }

    private RegistryScriptInvocation CreateInvocation(RegistryTweakDefinition definition, RegistryOperationDefinition operation, bool targetState, IReadOnlyDictionary<string, object?>? selectionParameters)
    {
        var scriptPath = ResolveAssetPath(operation.Script);
        var parameters = MergeParameters(operation.Parameters, selectionParameters);
        return new RegistryScriptInvocation(definition.Id, definition.Name, targetState, scriptPath, parameters);
    }

    private static IReadOnlyDictionary<string, object?> MergeParameters(
        IReadOnlyDictionary<string, object?>? baseParameters,
        IReadOnlyDictionary<string, object?>? overrideParameters)
    {
        var hasBase = baseParameters is not null && baseParameters.Count > 0;
        var hasOverride = overrideParameters is not null && overrideParameters.Count > 0;

        if (!hasBase && !hasOverride)
        {
            return ImmutableDictionary<string, object?>.Empty;
        }

        if (!hasOverride)
        {
            return baseParameters!;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (hasBase)
        {
            foreach (var pair in baseParameters!)
            {
                builder[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in overrideParameters!)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            builder[pair.Key] = pair.Value;
        }

        return builder.ToImmutable();
    }

    private static RegistryOptimizerConfiguration LoadConfiguration()
    {
        var path = ResolveAssetPath(ConfigurationRelativePath);
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var root = document.RootElement;
        var tweaks = ParseTweaks(root);
        var presets = ParsePresets(root);

        var lookup = tweaks.ToImmutableDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        return new RegistryOptimizerConfiguration(tweaks, presets, lookup);
    }

    private static ImmutableArray<RegistryTweakDefinition> ParseTweaks(JsonElement root)
    {
        if (!root.TryGetProperty("tweaks", out var tweaksElement) || tweaksElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Registry optimizer configuration is missing 'tweaks' array.");
        }

        var builder = ImmutableArray.CreateBuilder<RegistryTweakDefinition>();

        foreach (var element in tweaksElement.EnumerateArray())
        {
            var id = element.GetProperty("id").GetString() ?? throw new InvalidOperationException("Tweak 'id' is required.");
            var name = element.GetProperty("name").GetString() ?? throw new InvalidOperationException($"Tweak '{id}' is missing 'name'.");
            var category = element.GetProperty("category").GetString() ?? "General";
            var summary = element.GetProperty("summary").GetString() ?? string.Empty;
            var riskLevel = element.GetProperty("riskLevel").GetString() ?? "Safe";
            var icon = element.GetProperty("icon").GetString() ?? "🧰";
            var defaultState = element.TryGetProperty("defaultState", out var defaultStateElement) && defaultStateElement.ValueKind == JsonValueKind.True;
            var documentationLink = element.TryGetProperty("documentationLink", out var docElement) && docElement.ValueKind == JsonValueKind.String
                ? docElement.GetString()
                : null;

            var constraints = element.TryGetProperty("constraints", out var constraintsElement) && constraintsElement.ValueKind == JsonValueKind.Object
                ? ParseConstraints(constraintsElement)
                : null;

            var detection = element.TryGetProperty("detection", out var detectionElement) && detectionElement.ValueKind == JsonValueKind.Object
                ? ParseDetection(detectionElement)
                : null;

            if (!element.TryGetProperty("operations", out var operationsElement) || operationsElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Tweak '{id}' must define 'operations'.");
            }

            var enableOperation = operationsElement.TryGetProperty("enable", out var enableElement) && enableElement.ValueKind == JsonValueKind.Object
                ? ParseOperation(enableElement)
                : null;

            var disableOperation = operationsElement.TryGetProperty("disable", out var disableElement) && disableElement.ValueKind == JsonValueKind.Object
                ? ParseOperation(disableElement)
                : null;

            if (enableOperation is null && disableOperation is null)
            {
                throw new InvalidOperationException($"Tweak '{id}' must define at least one enable or disable operation.");
            }

            builder.Add(new RegistryTweakDefinition(id, name, category, summary, riskLevel, icon, defaultState, documentationLink, constraints, detection, enableOperation, disableOperation));
        }

        return builder.ToImmutable();
    }

    private static RegistryOperationDefinition ParseOperation(JsonElement element)
    {
        var script = element.GetProperty("script").GetString();
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new InvalidOperationException("Operation 'script' cannot be empty.");
        }

        IReadOnlyDictionary<string, object?>? parameters = null;
        if (element.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Object)
        {
            parameters = ParseParameters(parametersElement);
        }

        return new RegistryOperationDefinition(script, parameters);
    }

    private static IReadOnlyDictionary<string, object?> ParseParameters(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = ConvertJsonValue(property.Value);
        }

        return dictionary.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? (longValue >= int.MinValue && longValue <= int.MaxValue ? longValue : longValue)
                : element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonValue(p.Value), StringComparer.OrdinalIgnoreCase),
            _ => element.ToString()
        };
    }

    private static RegistryTweakConstraints ParseConstraints(JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        var min = element.TryGetProperty("min", out var minElement) && minElement.ValueKind == JsonValueKind.Number
            ? minElement.GetDouble()
            : (double?)null;

        var max = element.TryGetProperty("max", out var maxElement) && maxElement.ValueKind == JsonValueKind.Number
            ? maxElement.GetDouble()
            : (double?)null;

        var @default = element.TryGetProperty("default", out var defaultElement) && defaultElement.ValueKind == JsonValueKind.Number
            ? defaultElement.GetDouble()
            : (double?)null;

        return new RegistryTweakConstraints(type, min, max, @default);
    }

    private static RegistryTweakDetection? ParseDetection(JsonElement element)
    {
        if (!element.TryGetProperty("values", out var valuesElement) || valuesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<RegistryValueDetection>();

        foreach (var valueElement in valuesElement.EnumerateArray())
        {
            var hive = valueElement.TryGetProperty("hive", out var hiveElement) && hiveElement.ValueKind == JsonValueKind.String
                ? hiveElement.GetString()
                : null;

            var key = valueElement.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString()
                : null;

            var valueName = valueElement.TryGetProperty("valueName", out var valueNameElement) && valueNameElement.ValueKind == JsonValueKind.String
                ? valueNameElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(hive))
            {
                throw new InvalidOperationException("Registry detection requires a 'hive' property.");
            }

            if (string.IsNullOrWhiteSpace(valueName))
            {
                throw new InvalidOperationException($"Registry detection for hive '{hive}' is missing 'valueName'.");
            }

            if (key is null)
            {
                key = string.Empty;
            }

            var valueType = valueElement.TryGetProperty("valueType", out var valueTypeElement) && valueTypeElement.ValueKind == JsonValueKind.String
                ? valueTypeElement.GetString() ?? "String"
                : "String";

            var supportsCustom = valueElement.TryGetProperty("supportsCustomValue", out var supportsElement) && supportsElement.ValueKind == JsonValueKind.True;

            object? recommended = null;
            if (valueElement.TryGetProperty("recommendedValue", out var recommendedElement))
            {
                recommended = ConvertJsonValue(recommendedElement);
            }

            var lookup = valueElement.TryGetProperty("lookupValueName", out var lookupElement) && lookupElement.ValueKind == JsonValueKind.String
                ? lookupElement.GetString()
                : null;

            builder.Add(new RegistryValueDetection(hive, key, valueName, valueType, supportsCustom, recommended, lookup));
        }

        return builder.Count == 0 ? null : new RegistryTweakDetection(builder.ToImmutable());
    }

    private static ImmutableArray<RegistryPresetDefinition> ParsePresets(JsonElement root)
    {
        if (!root.TryGetProperty("presets", out var presetsElement) || presetsElement.ValueKind != JsonValueKind.Array)
        {
            return ImmutableArray<RegistryPresetDefinition>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<RegistryPresetDefinition>();

        foreach (var element in presetsElement.EnumerateArray())
        {
            var id = element.GetProperty("id").GetString() ?? throw new InvalidOperationException("Preset 'id' is required.");
            var name = element.GetProperty("name").GetString() ?? throw new InvalidOperationException($"Preset '{id}' is missing 'name'.");
            var description = element.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            var icon = element.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind == JsonValueKind.String
                ? iconElement.GetString() ?? "🧰"
                : "🧰";
            var isDefault = element.TryGetProperty("isDefault", out var defaultElement) && defaultElement.ValueKind == JsonValueKind.True;

            if (!element.TryGetProperty("states", out var statesElement) || statesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Preset '{id}' must define 'states'.");
            }

            var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in statesElement.EnumerateObject())
            {
                states[property.Name] = property.Value.ValueKind == JsonValueKind.True;
            }

            builder.Add(new RegistryPresetDefinition(id, name, description, icon, isDefault, states.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)));
        }

        return builder.ToImmutable();
    }

    private static string ResolveAssetPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Path must be provided.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{relativePath}'.", relativePath);
    }

    private sealed record RegistryOptimizerConfiguration(
        ImmutableArray<RegistryTweakDefinition> Tweaks,
        ImmutableArray<RegistryPresetDefinition> Presets,
        IReadOnlyDictionary<string, RegistryTweakDefinition> TweakLookup);
}

public sealed record RegistrySelection(
    string TweakId,
    bool TargetState,
    bool PreviousState,
    IReadOnlyDictionary<string, object?>? TargetParameters = null,
    IReadOnlyDictionary<string, object?>? PreviousParameters = null);

public sealed record RegistryRestorePoint(
    Guid Id,
    string FilePath,
    DateTimeOffset CreatedUtc,
    ImmutableArray<RegistryRestoreSelection> Selections,
    ImmutableArray<RegistryRestoreOperation> Operations)
{
    public bool HasOperations => Operations.Length > 0;
}

public sealed record RegistryRestoreSelection(string TweakId, bool PreviousState, bool TargetState);

public sealed record RegistryRestoreOperation(
    string TweakId,
    string Name,
    bool TargetState,
    string ScriptPath,
    ImmutableDictionary<string, string?> Parameters);

public sealed record RegistryOperationPlan(
    ImmutableArray<RegistryScriptInvocation> ApplyOperations,
    ImmutableArray<RegistryScriptInvocation> RevertOperations)
{
    public bool HasWork => ApplyOperations.Length > 0;
}

public sealed record RegistryScriptInvocation(
    string TweakId,
    string Name,
    bool TargetState,
    string ScriptPath,
    IReadOnlyDictionary<string, object?> Parameters);

public sealed record RegistryOperationResult(ImmutableArray<RegistryExecutionSummary> Executions)
{
    public bool IsSuccess => Executions.All(e => e.IsSuccess);

    public int SucceededCount => Executions.Count(e => e.IsSuccess);

    public int FailedCount => Executions.Length - SucceededCount;

    public IReadOnlyList<string> AggregateErrors()
        => Executions.Where(e => !e.IsSuccess).SelectMany(e => e.Errors).ToList();
}

public sealed record RegistryExecutionSummary(
    RegistryScriptInvocation Invocation,
    ImmutableArray<string> Output,
    ImmutableArray<string> Errors,
    int ExitCode)
{
    public bool IsSuccess => ExitCode == 0;
}

public sealed record RegistryTweakDefinition(
    string Id,
    string Name,
    string Category,
    string Summary,
    string RiskLevel,
    string Icon,
    bool DefaultState,
    string? DocumentationLink,
    RegistryTweakConstraints? Constraints,
    RegistryTweakDetection? Detection,
    RegistryOperationDefinition? EnableOperation,
    RegistryOperationDefinition? DisableOperation)
{
    public RegistryOperationDefinition? ResolveOperation(bool state)
        => state ? EnableOperation : DisableOperation;
}

public sealed record RegistryTweakDetection(ImmutableArray<RegistryValueDetection> Values);

public sealed record RegistryValueDetection(
    string Hive,
    string Key,
    string ValueName,
    string ValueType,
    bool SupportsCustomValue,
    object? RecommendedValue,
    string? LookupValueName);

public sealed record RegistryTweakConstraints(
    string? Type,
    double? Min,
    double? Max,
    double? Default);

public sealed record RegistryOperationDefinition(
    string Script,
    IReadOnlyDictionary<string, object?>? Parameters);

public sealed record RegistryPresetDefinition(
    string Id,
    string Name,
    string Description,
    string Icon,
    bool IsDefault,
    IReadOnlyDictionary<string, bool> States)
{
    public bool TryGetState(string tweakId, out bool state) => States.TryGetValue(tweakId, out state);
}
