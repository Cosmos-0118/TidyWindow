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
/// Executes maintenance automation for catalog-managed packages such as update or removal workflows.
/// </summary>
public sealed class PackageMaintenanceService
{
    private const string UpdateScriptRelativePath = "automation/scripts/update-catalog-package.ps1";
    private const string UpdateScriptOverrideEnvironmentVariable = "TIDYWINDOW_PACKAGE_UPDATE_SCRIPT";
    private const string RemoveScriptRelativePath = "automation/scripts/remove-catalog-package.ps1";
    private const string RemoveScriptOverrideEnvironmentVariable = "TIDYWINDOW_PACKAGE_REMOVE_SCRIPT";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly PowerShellInvoker _powerShellInvoker;

    public PackageMaintenanceService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
    }

    /// <summary>
    /// Runs the catalog-aware update script for the specified package.
    /// </summary>
    public async Task<PackageMaintenanceResult> UpdateAsync(PackageMaintenanceRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateRequest(request);

        var scriptPath = ResolveScriptPath(UpdateScriptRelativePath, UpdateScriptOverrideEnvironmentVariable);
        var parameters = BuildParameters(request);

        var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Package update script failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var payload = ParseResult(result.Output);
        return MapToResult(payload);
    }

    /// <summary>
    /// Runs the catalog-aware removal script for the specified package.
    /// </summary>
    public Task<PackageMaintenanceResult> RemoveAsync(PackageMaintenanceRequest request, CancellationToken cancellationToken = default)
    {
        return RemoveInternalAsync(request, forceCleanup: false, cancellationToken);
    }

    public Task<PackageMaintenanceResult> ForceRemoveAsync(PackageMaintenanceRequest request, CancellationToken cancellationToken = default)
    {
        return RemoveInternalAsync(request, forceCleanup: true, cancellationToken);
    }

    private async Task<PackageMaintenanceResult> RemoveInternalAsync(PackageMaintenanceRequest request, bool forceCleanup, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidateRequest(request);

        var scriptPath = ResolveScriptPath(RemoveScriptRelativePath, RemoveScriptOverrideEnvironmentVariable);
        var parameters = BuildParameters(request, forceCleanup);

        var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Package removal script failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var payload = ParseResult(result.Output);
        return MapToResult(payload);
    }

    private static void ValidateRequest(PackageMaintenanceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Manager))
        {
            throw new ArgumentException("Manager must be provided.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PackageId))
        {
            throw new ArgumentException("Package identifier must be provided.", nameof(request));
        }
    }

    private static Dictionary<string, object?> BuildParameters(PackageMaintenanceRequest request, bool forceCleanup = false)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Manager"] = request.Manager,
            ["PackageId"] = request.PackageId
        };

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            parameters["DisplayName"] = request.DisplayName;
        }

        if (request.RequiresAdministrator)
        {
            parameters["RequiresAdmin"] = true;
        }

        if (!string.IsNullOrWhiteSpace(request.RequestedVersion))
        {
            parameters["TargetVersion"] = request.RequestedVersion.Trim();
        }

        if (forceCleanup)
        {
            parameters["ForceCleanup"] = true;
        }

        return parameters;
    }

    private static PackageMaintenanceScriptResult ParseResult(IReadOnlyList<string> output)
    {
        if (output is null || output.Count == 0)
        {
            throw new InvalidOperationException("Maintenance script returned no output.");
        }

        var json = JsonPayloadExtractor.ExtractLastJsonBlock(output);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Maintenance script did not produce a JSON payload.");
        }

        try
        {
            return JsonSerializer.Deserialize<PackageMaintenanceScriptResult>(json, _jsonOptions)
                   ?? throw new InvalidOperationException("Maintenance script returned an empty payload.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Maintenance script returned invalid JSON.", ex);
        }
    }

    private static string ResolveScriptPath(string relativePath, string overrideVariable)
    {
        var overridePath = Environment.GetEnvironmentVariable(overrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
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

        throw new FileNotFoundException($"Unable to locate automation script at '{relativePath}'.", relativePath);
    }

    private static PackageMaintenanceResult MapToResult(PackageMaintenanceScriptResult payload)
    {
        var output = payload.Output is null
            ? ImmutableArray<string>.Empty
            : payload.Output.Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => line.Trim())
                .ToImmutableArray();

        var errors = payload.Errors is null
            ? ImmutableArray<string>.Empty
            : payload.Errors.Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => line.Trim())
                .ToImmutableArray();

        var summary = string.IsNullOrWhiteSpace(payload.Summary)
            ? (payload.Succeeded ? "Operation completed successfully." : "Operation reported a failure.")
            : payload.Summary.Trim();

        var requestedVersion = string.IsNullOrWhiteSpace(payload.RequestedVersion)
            ? null
            : payload.RequestedVersion.Trim();

        return new PackageMaintenanceResult(
            payload.Operation ?? string.Empty,
            payload.Manager ?? string.Empty,
            payload.PackageId ?? string.Empty,
            payload.Succeeded,
            summary,
            requestedVersion,
            payload.StatusBefore,
            payload.StatusAfter,
            payload.InstalledVersion,
            payload.LatestVersion,
            payload.Attempted,
            output,
            errors,
            payload.ExitCode ?? 0,
            payload.LogFile);
    }

    private sealed class PackageMaintenanceScriptResult
    {
        public string? Operation { get; set; }

        public string? Manager { get; set; }

        public string? PackageId { get; set; }

        public string? DisplayName { get; set; }

        public string? StatusBefore { get; set; }

        public string? StatusAfter { get; set; }

        public string? InstalledVersion { get; set; }

        public string? LatestVersion { get; set; }

        public bool Succeeded { get; set; }

        public bool Attempted { get; set; }

        public int? ExitCode { get; set; }

        public string? Summary { get; set; }

        public string? RequestedVersion { get; set; }

        public List<string>? Output { get; set; }

        public List<string>? Errors { get; set; }

        public string? LogFile { get; set; }
    }
}

public sealed record PackageMaintenanceRequest(
    string Manager,
    string PackageId,
    string DisplayName,
    bool RequiresAdministrator,
    string? RequestedVersion);

public sealed record PackageMaintenanceResult(
    string Operation,
    string Manager,
    string PackageId,
    bool Success,
    string Summary,
    string? RequestedVersion,
    string? StatusBefore,
    string? StatusAfter,
    string? InstalledVersion,
    string? LatestVersion,
    bool Attempted,
    ImmutableArray<string> Output,
    ImmutableArray<string> Errors,
    int ExitCode,
    string? LogFilePath);
