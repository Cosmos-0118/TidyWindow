using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.ProjectOblivion;

public sealed class ProjectOblivionInventoryService
{
    private const string ScriptRelativePath = "automation/scripts/get-installed-app-footprint.ps1";
    private const string OverrideEnvironmentVariable = "TIDYWINDOW_OBLIVION_INVENTORY_SCRIPT";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly PowerShellInvoker _powerShellInvoker;

    public ProjectOblivionInventoryService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
    }

    public async Task<ProjectOblivionInventorySnapshot> GetInventoryAsync(string? outputPath = null, CancellationToken cancellationToken = default)
    {
        var scriptPath = ResolveScriptPath();
        IReadOnlyDictionary<string, object?>? parameters = null;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["OutputPath"] = outputPath
            };
        }

        var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var detail = result.Errors.Count > 0 ? string.Join(Environment.NewLine, result.Errors) : "Unknown error.";
            throw new InvalidOperationException("Project Oblivion inventory script failed: " + detail);
        }

        var payload = DeserializePayload(result.Output);
        return MapSnapshot(payload);
    }

    private static ScriptPayload DeserializePayload(IReadOnlyList<string> output)
    {
        var json = ExtractJsonPayload(output);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Project Oblivion inventory script returned no JSON payload.");
        }

        try
        {
            return JsonSerializer.Deserialize<ScriptPayload>(json, _jsonOptions) ?? new ScriptPayload();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Project Oblivion inventory script returned invalid JSON.", ex);
        }
    }

    private static ProjectOblivionInventorySnapshot MapSnapshot(ScriptPayload payload)
    {
        var generatedAt = TryParseTimestamp(payload.GeneratedAt) ?? DateTimeOffset.UtcNow;
        var apps = payload.Apps is null
            ? ImmutableArray<ProjectOblivionApp>.Empty
            : payload.Apps
                .Select(MapApp)
                .Where(app => app is not null)
                .Select(app => app!)
                .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

        var warnings = payload.Warnings is null
            ? ImmutableArray<string>.Empty
            : payload.Warnings
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Select(warning => warning.Trim())
                .ToImmutableArray();

        return new ProjectOblivionInventorySnapshot(apps, warnings, generatedAt);
    }

    private static ProjectOblivionApp? MapApp(ScriptApp app)
    {
        if (app is null || string.IsNullOrWhiteSpace(app.AppId) || string.IsNullOrWhiteSpace(app.Name))
        {
            return null;
        }

        var installRoots = app.InstallRoots is null
            ? ImmutableArray<string>.Empty
            : app.InstallRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => root.Trim())
                .ToImmutableArray();

        var artifactHints = app.ArtifactHints is null
            ? ImmutableArray<string>.Empty
            : app.ArtifactHints
                .Where(hint => !string.IsNullOrWhiteSpace(hint))
                .Select(hint => hint.Trim())
                .ToImmutableArray();

        var processHints = app.ProcessHints is null
            ? ImmutableArray<string>.Empty
            : app.ProcessHints
                .Where(hint => !string.IsNullOrWhiteSpace(hint))
                .Select(hint => hint.Trim())
                .ToImmutableArray();

        var serviceHints = app.ServiceHints is null
            ? ImmutableArray<string>.Empty
            : app.ServiceHints
                .Where(hint => !string.IsNullOrWhiteSpace(hint))
                .Select(hint => hint.Trim())
                .ToImmutableArray();

        var managerHints = app.ManagerHints is null
            ? ImmutableArray<ProjectOblivionManagerHint>.Empty
            : app.ManagerHints
                .Where(hint => !string.IsNullOrWhiteSpace(hint.Manager) && !string.IsNullOrWhiteSpace(hint.PackageId))
                .Select(hint => new ProjectOblivionManagerHint(
                    hint.Manager!.Trim(),
                    hint.PackageId!.Trim(),
                    NormalizeValue(hint.InstalledVersion),
                    NormalizeValue(hint.AvailableVersion),
                    NormalizeValue(hint.Source)))
                .ToImmutableArray();

        var tags = app.Tags is null
            ? ImmutableArray<string>.Empty
            : app.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .ToImmutableArray();

        ProjectOblivionRegistryInfo? registry = null;
        if (app.Registry is not null)
        {
            registry = new ProjectOblivionRegistryInfo(
                NormalizeValue(app.Registry.Hive),
                NormalizeValue(app.Registry.KeyPath),
                NormalizeValue(app.Registry.DisplayIcon),
                NormalizeValue(app.Registry.InstallDate),
                NormalizeValue(app.Registry.InstallLocation));
        }

        return new ProjectOblivionApp(
            app.AppId.Trim(),
            app.Name.Trim(),
            NormalizeValue(app.Version),
            NormalizeValue(app.Publisher),
            NormalizeValue(app.Source),
            NormalizeValue(app.Scope),
            NormalizeValue(app.InstallRoot),
            installRoots,
            NormalizeValue(app.UninstallCommand),
            NormalizeValue(app.QuietUninstallCommand),
            NormalizeValue(app.PackageFamilyName),
            app.EstimatedSizeBytes,
            artifactHints,
            managerHints,
            processHints,
            serviceHints,
            registry,
            tags,
            NormalizeValue(app.Confidence));
    }

    private static string? ExtractJsonPayload(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            trimmed = trimmed.TrimStart('\uFEFF');
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string ResolveScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, ScriptRelativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, ScriptRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate automation script at '{ScriptRelativePath}'.");
    }

    private sealed class ScriptPayload
    {
        public string? GeneratedAt { get; set; }

        public List<string>? Warnings { get; set; }

        public List<ScriptApp>? Apps { get; set; }
    }

    private sealed class ScriptApp
    {
        public string? AppId { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? Source { get; set; }
        public string? Scope { get; set; }
        public string? InstallRoot { get; set; }
        public List<string>? InstallRoots { get; set; }
        public string? UninstallCommand { get; set; }
        public string? QuietUninstallCommand { get; set; }
        public string? PackageFamilyName { get; set; }
        public long? EstimatedSizeBytes { get; set; }
        public List<string>? ArtifactHints { get; set; }
        public List<ScriptManagerHint>? ManagerHints { get; set; }
        public List<string>? ProcessHints { get; set; }
        public List<string>? ServiceHints { get; set; }
        public ScriptRegistryInfo? Registry { get; set; }
        public List<string>? Tags { get; set; }
        public string? Confidence { get; set; }
    }

    private sealed class ScriptManagerHint
    {
        public string? Manager { get; set; }
        public string? PackageId { get; set; }
        public string? InstalledVersion { get; set; }
        public string? AvailableVersion { get; set; }
        public string? Source { get; set; }
    }

    private sealed class ScriptRegistryInfo
    {
        public string? Hive { get; set; }
        public string? KeyPath { get; set; }
        public string? DisplayIcon { get; set; }
        public string? InstallDate { get; set; }
        public string? InstallLocation { get; set; }
    }
}
