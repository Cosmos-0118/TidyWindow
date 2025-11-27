using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Uninstall;

public sealed class AppInventoryService : IAppInventoryService
{
    private const string ScriptRelativePath = "automation/scripts/get-installed-apps.ps1";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly TimeSpan _cacheDuration;
    private readonly object _syncRoot = new();

    private AppInventorySnapshot? _cachedSnapshot;
    private AppInventoryOptions _cachedOptions = AppInventoryOptions.Default;
    private string? _scriptPath;

    static AppInventoryService()
    {
        _jsonOptions.Converters.Add(new FlexibleStringConverter());
    }

    public AppInventoryService(PowerShellInvoker powerShellInvoker, TimeSpan? cacheDuration = null)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _cacheDuration = cacheDuration is { } duration && duration > TimeSpan.Zero
            ? duration
            : TimeSpan.FromMinutes(2);
    }

    public async Task<AppInventorySnapshot> GetInventoryAsync(AppInventoryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? AppInventoryOptions.Default;
        var normalizedOptions = effectiveOptions with { ForceRefresh = false, DryRun = false };

        if (!effectiveOptions.ForceRefresh && !effectiveOptions.DryRun)
        {
            lock (_syncRoot)
            {
                if (_cachedSnapshot is not null && _cachedOptions == normalizedOptions)
                {
                    var age = DateTimeOffset.UtcNow - _cachedSnapshot.GeneratedAt;
                    if (age <= _cacheDuration)
                    {
                        return _cachedSnapshot with { IsCacheHit = true };
                    }
                }
            }
        }

        var snapshot = await ExecuteInventoryAsync(effectiveOptions, cancellationToken).ConfigureAwait(false);

        if (!snapshot.IsDryRun)
        {
            lock (_syncRoot)
            {
                _cachedSnapshot = snapshot with { IsCacheHit = false };
                _cachedOptions = normalizedOptions;
            }
        }

        return snapshot;
    }

    private async Task<AppInventorySnapshot> ExecuteInventoryAsync(AppInventoryOptions options, CancellationToken cancellationToken)
    {
        var scriptPath = ResolveScriptPath();
        var parameters = BuildParameterSet(options);

        var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Installed app inventory script failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var payloadJson = JsonPayloadExtractor.ExtractLastJsonBlock(result.Output);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new InvalidOperationException("Installed app inventory script returned no JSON payload.");
        }

        AppInventoryScriptPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AppInventoryScriptPayload>(payloadJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Installed app inventory script returned invalid JSON.", ex);
        }

        if (payload is null)
        {
            throw new InvalidOperationException("Installed app inventory payload was empty.");
        }

        var generatedAt = TryParseTimestamp(payload.GeneratedAt) ?? DateTimeOffset.UtcNow;
        var duration = payload.DurationMs.HasValue && payload.DurationMs.Value > 0
            ? TimeSpan.FromMilliseconds(payload.DurationMs.Value)
            : TimeSpan.Zero;

        var warnings = payload.Warnings is null
            ? ImmutableArray<string>.Empty
            : payload.Warnings
                .Where(static warning => !string.IsNullOrWhiteSpace(warning))
                .Select(static warning => warning.Trim())
                .ToImmutableArray();

        var plan = payload.Plan is null
            ? ImmutableArray<string>.Empty
            : payload.Plan
                .Where(static step => !string.IsNullOrWhiteSpace(step))
                .Select(static step => step.Trim())
                .ToImmutableArray();

        var apps = payload.Apps is null
            ? ImmutableArray<InstalledApp>.Empty
            : payload.Apps
                .Select(MapApp)
                .Where(static app => app is not null)
                .Select(static app => app!)
                .ToImmutableArray();

        return new AppInventorySnapshot(
            apps,
            warnings,
            generatedAt,
            duration,
            payload.IsDryRun,
            isCacheHit: false,
            options,
            plan);
    }

    private Dictionary<string, object?> BuildParameterSet(AppInventoryOptions options)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (options.IncludeSystemComponents)
        {
            parameters["IncludeSystemComponents"] = true;
        }

        if (options.IncludeUpdates)
        {
            parameters["IncludeUpdates"] = true;
        }

        if (!options.IncludeWinget)
        {
            parameters["IncludeWinget"] = false;
        }

        if (!options.IncludeUserEntries)
        {
            parameters["IncludeUserEntries"] = false;
        }

        if (options.DryRun)
        {
            parameters["PlanOnly"] = true;
        }

        return parameters;
    }

    private string ResolveScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(_scriptPath) && File.Exists(_scriptPath))
        {
            return _scriptPath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, ScriptRelativePath);
        if (File.Exists(candidate))
        {
            _scriptPath = candidate;
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, ScriptRelativePath);
            if (File.Exists(candidate))
            {
                _scriptPath = candidate;
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate automation script at '{ScriptRelativePath}'.");
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static ImmutableArray<string> NormalizeArray(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return ImmutableArray<string>.Empty;
        }

        return values
            .Select(Normalize)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static ImmutableDictionary<string, string> NormalizeMetadata(IDictionary<string, string?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in metadata)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
            {
                continue;
            }

            builder[kvp.Key.Trim()] = kvp.Value?.Trim() ?? string.Empty;
        }

        return builder.ToImmutable();
    }

    private static InstalledApp? MapApp(AppInventoryScriptApp source)
    {
        if (source is null)
        {
            return null;
        }

        var name = Normalize(source.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new InstalledApp(
            name,
            Normalize(source.Version),
            Normalize(source.Publisher),
            Normalize(source.InstallLocation),
            Normalize(source.UninstallString),
            Normalize(source.QuietUninstallString),
            source.WindowsInstaller,
            Normalize(source.ProductCode),
            Normalize(source.InstallerType) ?? "Unknown",
            NormalizeArray(source.SourceTags),
            NormalizeArray(source.InstallerHints),
            Normalize(source.RegistryKey),
            source.SystemComponent,
            Normalize(source.ReleaseType),
            source.EstimatedSizeBytes,
            Normalize(source.InstallDate),
            Normalize(source.DisplayIcon),
            Normalize(source.Language),
            Normalize(source.WingetId),
            Normalize(source.WingetSource),
            Normalize(source.WingetVersion),
            Normalize(source.WingetAvailableVersion),
            NormalizeMetadata(source.Metadata));
    }

    private sealed class AppInventoryScriptPayload
    {
        public string? GeneratedAt { get; set; }

        public long? DurationMs { get; set; }

        public bool IsDryRun { get; set; }

        public List<string>? Plan { get; set; }

        public List<string>? Warnings { get; set; }

        public List<AppInventoryScriptApp>? Apps { get; set; }
    }

    private sealed class AppInventoryScriptApp
    {
        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Publisher { get; set; }

        public string? InstallLocation { get; set; }

        public string? UninstallString { get; set; }

        public string? QuietUninstallString { get; set; }

        public bool WindowsInstaller { get; set; }

        public string? ProductCode { get; set; }

        public string? InstallerType { get; set; }

        public List<string>? SourceTags { get; set; }

        [JsonConverter(typeof(FlexibleStringListConverter))]
        public List<string>? InstallerHints { get; set; }

        public string? RegistryKey { get; set; }

        public bool SystemComponent { get; set; }

        public string? ReleaseType { get; set; }

        public long? EstimatedSizeBytes { get; set; }

        public string? InstallDate { get; set; }

        public string? DisplayIcon { get; set; }

        public string? Language { get; set; }

        public string? WingetId { get; set; }

        public string? WingetSource { get; set; }

        public string? WingetVersion { get; set; }

        public string? WingetAvailableVersion { get; set; }

        public Dictionary<string, string?>? Metadata { get; set; }
    }

    private sealed class FlexibleStringListConverter : JsonConverter<List<string>?>
    {
        public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var value = reader.GetString();
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : new List<string> { value };
            }

            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Expected string or array when parsing installer hints.");
            }

            var list = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var text = element.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Number || element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
                {
                    list.Add(element.ToString());
                }
            }

            return list.Count == 0 ? null : list;
        }

        public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
        {
            if (value is null || value.Count == 0)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            foreach (var item in value)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }
    }

    private sealed class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }

            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.ToString();
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(value);
        }
    }
}
