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

public sealed class PackageVersionDiscoveryService
{
    private const string ScriptRelativePath = "automation/scripts/get-package-versions.ps1";
    private const string ScriptOverrideEnvironmentVariable = "TIDYWINDOW_PACKAGE_VERSION_SCRIPT";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly PowerShellInvoker _powerShellInvoker;

    public PackageVersionDiscoveryService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
    }

    public async Task<PackageVersionDiscoveryResult> GetVersionsAsync(
        string manager,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manager))
        {
            throw new ArgumentException("Manager must be provided.", nameof(manager));
        }

        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new ArgumentException("Package identifier must be provided.", nameof(packageId));
        }

        var scriptPath = ResolveScriptPath();
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Manager"] = manager,
            ["PackageId"] = packageId
        };

        var result = await _powerShellInvoker
            .InvokeScriptAsync(scriptPath, parameters, cancellationToken)
            .ConfigureAwait(false);

        var json = JsonPayloadExtractor.ExtractLastJsonBlock(result.Output);
        if (string.IsNullOrWhiteSpace(json))
        {
            var errorText = result.Errors.Count > 0
                ? string.Join(Environment.NewLine, result.Errors)
                : "Version discovery script did not return structured output.";
            throw new InvalidOperationException(errorText);
        }

        VersionDiscoveryPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<VersionDiscoveryPayload>(json, _jsonOptions)
                      ?? new VersionDiscoveryPayload();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Version discovery script returned invalid JSON.", ex);
        }

        var versions = payload.Versions is null
            ? ImmutableArray<string>.Empty
            : payload.Versions
                .Where(static version => !string.IsNullOrWhiteSpace(version))
                .Select(static version => version.Trim())
                .ToImmutableArray();

        var success = payload.Succeeded;
        var errorMessage = string.IsNullOrWhiteSpace(payload.Error)
            ? null
            : payload.Error.Trim();

        return new PackageVersionDiscoveryResult(
            success,
            payload.Manager ?? manager,
            payload.PackageId ?? packageId,
            versions,
            errorMessage);
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

        throw new FileNotFoundException(
            $"Unable to locate version discovery automation script at '{ScriptRelativePath}'.",
            ScriptRelativePath);
    }

    private sealed class VersionDiscoveryPayload
    {
        public bool Succeeded { get; set; }

        public string? Manager { get; set; }

        public string? PackageId { get; set; }

        public List<string>? Versions { get; set; }

        public string? Error { get; set; }
    }
}

public sealed record PackageVersionDiscoveryResult(
    bool Success,
    string Manager,
    string PackageId,
    ImmutableArray<string> Versions,
    string? ErrorMessage);
