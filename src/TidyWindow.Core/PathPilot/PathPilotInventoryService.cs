using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.PathPilot;

/// <summary>
/// Executes the PathPilot inventory automation script and maps the result into strongly typed models for the app surface.
/// </summary>
public sealed class PathPilotInventoryService
{
    private const string ScriptRelativePath = "automation/scripts/Get-PathPilotInventory.ps1";
    private const string ScriptOverrideEnvironmentVariable = "TIDYWINDOW_PATHPILOT_SCRIPT";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly PowerShellInvoker _powerShellInvoker;

    public PathPilotInventoryService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
    }

    /// <summary>
    /// Executes the automation script and returns the current runtime inventory snapshot.
    /// </summary>
    public async Task<PathPilotInventorySnapshot> GetInventoryAsync(string? configOverridePath = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(configOverridePath))
        {
            parameters["ConfigPath"] = configOverridePath;
        }

        var result = await RunScriptAsync(parameters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "PathPilot inventory script failed: ");
        var payload = DeserializePayload(result.Output, "PathPilot inventory");
        return MapSnapshot(payload);
    }

    /// <summary>
    /// Switches the active runtime by invoking the automation script and returns the refreshed snapshot plus switch metadata.
    /// </summary>
    public async Task<PathPilotSwitchOperationResult> SwitchRuntimeAsync(PathPilotSwitchRequest request, string? configOverridePath = null, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RuntimeId))
        {
            throw new ArgumentException("Runtime identifier is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            throw new ArgumentException("Installation path is required for switching.", nameof(request));
        }

        var parameters = new Dictionary<string, object?>
        {
            ["SwitchRuntimeId"] = request.RuntimeId,
            ["SwitchInstallPath"] = request.ExecutablePath
        };

        if (!string.IsNullOrWhiteSpace(configOverridePath))
        {
            parameters["ConfigPath"] = configOverridePath;
        }

        var result = await RunScriptAsync(parameters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "PathPilot switch script failed: ");
        var payload = DeserializePayload(result.Output, "PathPilot switch");

        if (payload.SwitchResult is null)
        {
            throw new InvalidOperationException("PathPilot switch script did not include switch metadata.");
        }

        var snapshot = MapSnapshot(payload);
        var switchResult = MapSwitchResult(payload.SwitchResult);
        return new PathPilotSwitchOperationResult(snapshot, switchResult);
    }

    /// <summary>
    /// Generates an export file (JSON or Markdown) by invoking the automation script and returns the resulting metadata.
    /// </summary>
    public async Task<PathPilotExportResult> ExportInventoryAsync(
        PathPilotExportFormat format,
        string? destinationPath = null,
        string? configOverridePath = null,
        CancellationToken cancellationToken = default)
    {
        var exportPath = ResolveExportPath(format, destinationPath);
        var parameters = new Dictionary<string, object?>
        {
            ["Export"] = format == PathPilotExportFormat.Markdown ? "markdown" : "json",
            ["OutputPath"] = exportPath
        };

        if (!string.IsNullOrWhiteSpace(configOverridePath))
        {
            parameters["ConfigPath"] = configOverridePath;
        }

        var result = await RunScriptAsync(parameters, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "PathPilot export script failed: ");

        DateTimeOffset generatedAt;
        if (format == PathPilotExportFormat.Json)
        {
            var payload = DeserializePayload(result.Output, "PathPilot export");
            generatedAt = TryParseTimestamp(payload.GeneratedAt) ?? DateTimeOffset.UtcNow;
        }
        else
        {
            generatedAt = ExtractMarkdownTimestamp(exportPath) ?? DateTimeOffset.UtcNow;
        }

        return new PathPilotExportResult(format, exportPath, generatedAt);
    }

    private async Task<PowerShellInvocationResult> RunScriptAsync(IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var scriptPath = ResolveScriptPath();
        return await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSuccess(PowerShellInvocationResult result, string failurePrefix)
    {
        if (result.IsSuccess)
        {
            return;
        }

        var detail = result.Errors.Count > 0
            ? string.Join(Environment.NewLine, result.Errors)
            : "Unknown error.";

        throw new InvalidOperationException(failurePrefix + detail);
    }

    private ScriptPayload DeserializePayload(IReadOnlyList<string> output, string context)
    {
        var jsonPayload = ExtractJsonPayload(output);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            throw new InvalidOperationException($"{context} script did not return a JSON payload.");
        }

        try
        {
            return JsonSerializer.Deserialize<ScriptPayload>(jsonPayload, _jsonOptions) ?? new ScriptPayload();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{context} script returned invalid JSON.", ex);
        }
    }

    private static PathPilotInventorySnapshot MapSnapshot(ScriptPayload payload)
    {
        var generatedAt = TryParseTimestamp(payload.GeneratedAt) ?? DateTimeOffset.UtcNow;
        var runtimes = payload.Runtimes is null
            ? ImmutableArray<PathPilotRuntime>.Empty
            : payload.Runtimes
                .Select(MapRuntime)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToImmutableArray();

        var warnings = payload.Warnings is null
            ? ImmutableArray<string>.Empty
            : payload.Warnings
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Select(warning => warning.Trim())
                .ToImmutableArray();

        var entries = payload.MachinePath?.Entries is null
            ? ImmutableArray<MachinePathEntry>.Empty
            : payload.MachinePath!.Entries
                .Where(entry => entry is not null)
                .Select(entry => new MachinePathEntry(entry!.Index, entry.Value ?? string.Empty, entry.Resolved))
                .ToImmutableArray();

        var machinePath = new MachinePathInfo(entries, payload.MachinePath?.Raw);
        return new PathPilotInventorySnapshot(runtimes, machinePath, warnings, generatedAt);
    }

    private static PathPilotSwitchResult MapSwitchResult(ScriptSwitchResult result)
    {
        var runtimeId = string.IsNullOrWhiteSpace(result.RuntimeId)
            ? "(unknown)"
            : result.RuntimeId.Trim();
        var timestamp = TryParseTimestamp(result.Timestamp) ?? DateTimeOffset.UtcNow;

        return new PathPilotSwitchResult(
            runtimeId,
            result.TargetDirectory ?? string.Empty,
            result.TargetExecutable ?? string.Empty,
            result.InstallationId,
            result.BackupPath,
            result.LogPath,
            result.PathUpdated,
            result.Success,
            string.IsNullOrWhiteSpace(result.Message) ? null : result.Message.Trim(),
            result.PreviousPath,
            result.UpdatedPath,
            timestamp);
    }

    private static string ResolveExportPath(PathPilotExportFormat format, string? destinationPath)
    {
        var extension = format == PathPilotExportFormat.Markdown ? ".md" : ".json";
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var defaultFileName = $"pathpilot-report-{timestamp}{extension}";
        string targetPath;

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            var exportsDirectory = Path.Combine(GetPathPilotDataDirectory(), "exports");
            Directory.CreateDirectory(exportsDirectory);
            targetPath = Path.Combine(exportsDirectory, defaultFileName);
        }
        else
        {
            var expanded = Environment.ExpandEnvironmentVariables(destinationPath.Trim());

            if (expanded.EndsWith(Path.DirectorySeparatorChar) || expanded.EndsWith(Path.AltDirectorySeparatorChar))
            {
                Directory.CreateDirectory(expanded);
                targetPath = Path.Combine(expanded, defaultFileName);
            }
            else if (Directory.Exists(expanded))
            {
                targetPath = Path.Combine(expanded, defaultFileName);
            }
            else
            {
                var directory = Path.GetDirectoryName(expanded);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                targetPath = Path.HasExtension(expanded) ? expanded : expanded + extension;
            }
        }

        return targetPath;
    }

    private static DateTimeOffset? ExtractMarkdownTimestamp(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            const string prefix = "Generated at:";
            foreach (var line in File.ReadLines(filePath).Take(5))
            {
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line.Substring(prefix.Length).Trim();
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Ignore file reading issues; caller will fall back to UtcNow.
        }

        return null;
    }

    private static string GetPathPilotDataDirectory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = @"C:\\ProgramData";
        }

        var directory = Path.Combine(programData, "TidyWindow", "PathPilot");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static PathPilotRuntime? MapRuntime(ScriptRuntime? runtime)
    {
        if (runtime is null)
        {
            return null;
        }

        var id = !string.IsNullOrWhiteSpace(runtime.Id)
            ? runtime.Id.Trim()
            : runtime.Name ?? runtime.ExecutableName ?? Guid.NewGuid().ToString("N");

        var name = !string.IsNullOrWhiteSpace(runtime.Name)
            ? runtime.Name.Trim()
            : id;

        if (string.IsNullOrWhiteSpace(runtime.ExecutableName))
        {
            return null;
        }

        var installations = runtime.Installations is null
            ? ImmutableArray<PathPilotInstallation>.Empty
            : runtime.Installations
                .Select(MapInstallation)
                .Where(install => install is not null)
                .Select(install => install!)
                .ToImmutableArray();

        var status = runtime.Status is null
            ? new PathPilotRuntimeStatus(false, false, false, false)
            : new PathPilotRuntimeStatus(
                runtime.Status.IsMissing,
                runtime.Status.HasDuplicates,
                runtime.Status.IsDrifted,
                runtime.Status.HasUnknownActive);

        var active = runtime.Active is null
            ? null
            : new PathPilotActiveResolution(
                runtime.Active.ExecutablePath,
                runtime.Active.PathEntry,
                runtime.Active.MatchesKnownInstallation,
                runtime.Active.InstallationId,
                runtime.Active.Source);

        var resolutionOrder = runtime.ResolutionOrder is null
            ? ImmutableArray<string>.Empty
            : runtime.ResolutionOrder
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Select(candidate => candidate.Trim())
                .ToImmutableArray();

        return new PathPilotRuntime(
            id,
            name,
            runtime.ExecutableName,
            runtime.DesiredVersion,
            runtime.Description,
            installations,
            status,
            active,
            resolutionOrder);
    }

    private static PathPilotInstallation? MapInstallation(ScriptInstallation? installation)
    {
        if (installation is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(installation.Id) || string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            return null;
        }

        var directory = string.IsNullOrWhiteSpace(installation.Directory)
            ? Path.GetDirectoryName(installation.ExecutablePath) ?? string.Empty
            : installation.Directory.Trim();

        var notes = installation.Notes is null
            ? ImmutableArray<string>.Empty
            : installation.Notes
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Select(note => note.Trim())
                .ToImmutableArray();

        return new PathPilotInstallation(
            installation.Id,
            directory,
            installation.ExecutablePath,
            NormalizeValue(installation.Version),
            NormalizeValue(installation.Architecture) ?? "unknown",
            NormalizeValue(installation.Source) ?? "unknown",
            installation.IsActive,
            notes);
    }

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ExtractJsonPayload(IEnumerable<string> lines)
    {
        if (lines is null)
        {
            return null;
        }

        foreach (var line in lines.Reverse())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.TrimStart('\uFEFF').Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string ResolveScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(ScriptOverrideEnvironmentVariable);
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

        public List<ScriptRuntime>? Runtimes { get; set; }

        public ScriptMachinePath? MachinePath { get; set; }

        public string? ExportPath { get; set; }

        public ScriptSwitchResult? SwitchResult { get; set; }
    }

    private sealed class ScriptSwitchResult
    {
        public string? RuntimeId { get; set; }

        public string? TargetDirectory { get; set; }

        public string? TargetExecutable { get; set; }

        public string? InstallationId { get; set; }

        public string? BackupPath { get; set; }

        public string? LogPath { get; set; }

        public bool PathUpdated { get; set; }

        public bool Success { get; set; }

        public string? Message { get; set; }

        public string? PreviousPath { get; set; }

        public string? UpdatedPath { get; set; }

        public string? Timestamp { get; set; }
    }

    private sealed class ScriptMachinePath
    {
        public string? Raw { get; set; }

        public List<ScriptMachinePathEntry>? Entries { get; set; }
    }

    private sealed class ScriptMachinePathEntry
    {
        public int Index { get; set; }

        public string? Value { get; set; }

        public string? Resolved { get; set; }
    }

    private sealed class ScriptRuntime
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? ExecutableName { get; set; }

        public string? DesiredVersion { get; set; }

        public string? Description { get; set; }

        public List<ScriptInstallation>? Installations { get; set; }

        public ScriptStatus? Status { get; set; }

        public ScriptActiveResolution? Active { get; set; }

        public List<string>? ResolutionOrder { get; set; }
    }

    private sealed class ScriptInstallation
    {
        public string? Id { get; set; }

        public string? Directory { get; set; }

        public string? ExecutablePath { get; set; }

        public string? Version { get; set; }

        public string? Architecture { get; set; }

        public string? Source { get; set; }

        public bool IsActive { get; set; }

        public List<string>? Notes { get; set; }
    }

    private sealed class ScriptStatus
    {
        public bool IsMissing { get; set; }

        public bool HasDuplicates { get; set; }

        public bool IsDrifted { get; set; }

        public bool HasUnknownActive { get; set; }
    }

    private sealed class ScriptActiveResolution
    {
        public string? ExecutablePath { get; set; }

        public string? PathEntry { get; set; }

        public bool MatchesKnownInstallation { get; set; }

        public string? InstallationId { get; set; }

        public string? Source { get; set; }
    }
}
