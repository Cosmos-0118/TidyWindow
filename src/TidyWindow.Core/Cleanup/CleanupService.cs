using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Coordinates execution of the cleanup-preview PowerShell script and materializes typed results.
/// </summary>
public sealed class CleanupService
{
    private readonly PowerShellInvoker _powerShellInvoker;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CleanupService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker;
    }

    public async Task<CleanupReport> PreviewAsync(bool includeDownloads, int previewCount, CleanupItemKind itemKind = CleanupItemKind.Files, CancellationToken cancellationToken = default)
    {
        if (previewCount < 0)
        {
            previewCount = 0;
        }

        var scriptPath = ResolveScriptPath(Path.Combine("automation", "scripts", "cleanup-preview.ps1"));

        var parameters = new Dictionary<string, object?>
        {
            ["PreviewCount"] = previewCount
        };

        if (includeDownloads)
        {
            parameters["IncludeDownloads"] = true;
        }

        parameters["ItemKind"] = itemKind switch
        {
            CleanupItemKind.Folders => "Folders",
            CleanupItemKind.Both => "Both",
            _ => "Files"
        };

        var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException("Cleanup preview failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var jsonPayload = ExtractJsonPayload(result.Output);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return CleanupReport.Empty;
        }

        try
        {
            var node = JsonNode.Parse(jsonPayload);
            if (node is not JsonArray array)
            {
                return CleanupReport.Empty;
            }

            var reports = array
                .Select(entry => entry?.Deserialize<CleanupTargetJson>(_jsonOptions))
                .Where(entry => entry is not null)
                .Select(entry => Map(entry!))
                .Where(mapped => mapped is not null)
                .Select(mapped => mapped!)
                .ToList();

            return new CleanupReport(reports);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Cleanup preview returned invalid JSON.", ex);
        }
    }

    public Task<CleanupDeletionResult> DeleteAsync(IEnumerable<CleanupPreviewItem> items, CancellationToken cancellationToken = default)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        var targets = items
            .Where(static item => !string.IsNullOrWhiteSpace(item.FullName))
            .Select(static item => item.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            return Task.FromResult(new CleanupDeletionResult(0, 0, Array.Empty<string>()));
        }

        return Task.Run(() => DeleteInternal(targets, cancellationToken), cancellationToken);
    }

    private static CleanupDeletionResult DeleteInternal(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    deleted++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        return new CleanupDeletionResult(deleted, skipped, errors);
    }

    private static CleanupTargetReport? Map(CleanupTargetJson json)
    {
        var previewItems = json.Preview?
            .Select(item =>
            {
                var lastModified = item.LastModified?.ToUniversalTime();
                return new CleanupPreviewItem(item.Name, item.FullName, item.SizeBytes, lastModified, item.IsDirectory, item.Extension);
            })
            .ToList();

        return new CleanupTargetReport(
            json.Category,
            json.Path,
            json.Exists,
            json.ItemCount,
            json.TotalSizeBytes,
            previewItems,
            json.Notes,
            json.DryRun,
            json.Classification);
    }

    private static string? ExtractJsonPayload(IEnumerable<string> outputLines)
    {
        foreach (var line in outputLines.Reverse())
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            trimmed = trimmed.TrimStart('\uFEFF');
            if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string ResolveScriptPath(string relativePath)
    {
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

        throw new FileNotFoundException($"Unable to locate automation script at relative path '{relativePath}'.");
    }

    private sealed class CleanupTargetJson
    {
        public string? Category { get; set; }
        public string? Classification { get; set; }
        public string? Path { get; set; }
        public bool Exists { get; set; }
        public int ItemCount { get; set; }
        public long TotalSizeBytes { get; set; }
        public bool DryRun { get; set; }
        public string? Notes { get; set; }
        public List<CleanupPreviewItemJson>? Preview { get; set; }
    }

    private sealed class CleanupPreviewItemJson
    {
        public string? Name { get; set; }
        public string? FullName { get; set; }
        public long SizeBytes { get; set; }
        public DateTime? LastModified { get; set; }
        public bool IsDirectory { get; set; }
        public string? Extension { get; set; }
    }
}
