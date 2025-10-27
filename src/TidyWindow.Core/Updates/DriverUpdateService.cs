using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Updates;

public sealed class DriverUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PowerShellInvoker _powerShellInvoker;

    public DriverUpdateService(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
    }

    public async Task<DriverUpdateScanResult> DetectAsync(bool includeOptional = false, CancellationToken cancellationToken = default)
    {
        var scriptPath = ResolveScriptPath(Path.Combine("automation", "essentials", "driver-update-detect.ps1"));

        var parameters = new Dictionary<string, object?>();
        if (includeOptional)
        {
            parameters["IncludeOptional"] = true;
        }

        var result = await _powerShellInvoker
            .InvokeScriptAsync(scriptPath, parameters, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess && result.Errors.Count > 0)
        {
            throw new InvalidOperationException("Driver update detection failed: " + string.Join(Environment.NewLine, result.Errors));
        }

        var jsonPayload = ExtractJsonPayload(result.Output);
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return new DriverUpdateScanResult(Array.Empty<DriverUpdateInfo>(), DateTimeOffset.UtcNow, NormalizeWarnings(result.Errors));
        }

        DriverUpdatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DriverUpdatePayload>(jsonPayload, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Driver update script returned invalid JSON.", ex);
        }

        var updates = MapUpdates(payload?.Updates);
        var generatedAt = ResolveTimestamp(payload?.GeneratedAtUtc) ?? DateTimeOffset.UtcNow;

        var warnings = NormalizeWarnings(result.Errors);
        if ((payload?.SkippedOptional ?? 0) > 0 && !(payload?.IncludeOptional ?? false))
        {
            warnings = warnings.Concat(new[] { $"Skipped {payload!.SkippedOptional} optional update(s)." }).ToArray();
        }

        return new DriverUpdateScanResult(updates, generatedAt, warnings);
    }

    private static IReadOnlyList<DriverUpdateInfo> MapUpdates(IEnumerable<DriverUpdateJson>? entries)
    {
        if (entries is null)
        {
            return Array.Empty<DriverUpdateInfo>();
        }

        var items = new List<DriverUpdateInfo>();

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var title = Normalize(entry.Title) ?? "Driver update";
            var deviceName = Normalize(entry.DeviceName) ?? title;
            var manufacturer = Normalize(entry.Manufacturer);

            var hardwareIds = entry.HardwareIds is null
                ? Array.Empty<string>()
                : entry.HardwareIds
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .Select(static id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            var currentVersion = Normalize(entry.CurrentVersion);
            var availableVersion = Normalize(entry.AvailableVersion);
            var currentDate = ResolveTimestamp(entry.CurrentVersionDate);
            var availableDate = ResolveTimestamp(entry.AvailableVersionDate);

            var categories = entry.Categories is null
                ? Array.Empty<string>()
                : entry.Categories
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            var informationLinks = entry.InformationUrls is null
                ? Array.Empty<Uri>()
                : entry.InformationUrls
                    .Select(TryCreateUri)
                    .Where(static uri => uri is not null)
                    .Cast<Uri>()
                    .ToArray();

            var description = Normalize(entry.Description);
            var status = DetermineStatus(currentVersion, availableVersion);
            var isOptional = entry.IsOptional ?? false;

            items.Add(new DriverUpdateInfo(
                title,
                deviceName,
                manufacturer,
                hardwareIds,
                currentVersion,
                currentDate,
                availableVersion,
                availableDate,
                categories,
                informationLinks,
                isOptional,
                status,
                description));
        }

        return items
            .OrderByDescending(static item => item.Status == DriverUpdateStatus.UpdateAvailable ? 1 : 0)
            .ThenBy(static item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static Uri? TryCreateUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? uri : null;
    }

    private static DateTimeOffset? ResolveTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return timestamp;
        }

        return null;
    }

    private static DriverUpdateStatus DetermineStatus(string? currentVersion, string? availableVersion)
    {
        if (string.IsNullOrWhiteSpace(availableVersion))
        {
            return string.IsNullOrWhiteSpace(currentVersion)
                ? DriverUpdateStatus.Unknown
                : DriverUpdateStatus.UpToDate;
        }

        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return DriverUpdateStatus.UpdateAvailable;
        }

        var comparison = CompareVersions(currentVersion, availableVersion);
        if (comparison is null)
        {
            return string.Equals(currentVersion.Trim(), availableVersion.Trim(), StringComparison.OrdinalIgnoreCase)
                ? DriverUpdateStatus.UpToDate
                : DriverUpdateStatus.Unknown;
        }

        return comparison < 0 ? DriverUpdateStatus.UpdateAvailable : DriverUpdateStatus.UpToDate;
    }

    private static int? CompareVersions(string currentVersion, string availableVersion)
    {
        if (Version.TryParse(currentVersion, out var current) && Version.TryParse(availableVersion, out var available))
        {
            return current.CompareTo(available);
        }

        var currentSegments = ParseSegments(currentVersion);
        var availableSegments = ParseSegments(availableVersion);

        if (currentSegments is null || availableSegments is null)
        {
            return null;
        }

        var length = Math.Max(currentSegments.Length, availableSegments.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < currentSegments.Length ? currentSegments[i] : 0;
            var right = i < availableSegments.Length ? availableSegments[i] : 0;

            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    private static int[]? ParseSegments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(new[] { '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var segments = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return null;
            }

            segments[i] = parsed;
        }

        return segments;
    }

    private static string[] NormalizeWarnings(IEnumerable<string> warnings)
    {
        if (warnings is null)
        {
            return Array.Empty<string>();
        }

        return warnings
            .Select(static warning => warning?.Trim())
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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

    private sealed class DriverUpdatePayload
    {
        public string? GeneratedAtUtc { get; set; }
        public bool? IncludeOptional { get; set; }
        public int? SkippedOptional { get; set; }
        public List<DriverUpdateJson>? Updates { get; set; }
    }

    private sealed class DriverUpdateJson
    {
        public string? Title { get; set; }
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string[]? HardwareIds { get; set; }
        public bool? IsOptional { get; set; }
        public string? CurrentVersion { get; set; }
        public string? CurrentVersionDate { get; set; }
        public string? AvailableVersion { get; set; }
        public string? AvailableVersionDate { get; set; }
        public string? Description { get; set; }
        public string[]? Categories { get; set; }
        public string[]? InformationUrls { get; set; }
    }
}

public enum DriverUpdateStatus
{
    Unknown,
    UpToDate,
    UpdateAvailable
}

public sealed record DriverUpdateInfo(
    string Title,
    string DeviceName,
    string? Manufacturer,
    IReadOnlyList<string> HardwareIds,
    string? CurrentVersion,
    DateTimeOffset? CurrentVersionDate,
    string? AvailableVersion,
    DateTimeOffset? AvailableVersionDate,
    IReadOnlyList<string> Categories,
    IReadOnlyList<Uri> InformationLinks,
    bool IsOptional,
    DriverUpdateStatus Status,
    string? Description);

public sealed record DriverUpdateScanResult(
    IReadOnlyList<DriverUpdateInfo> Updates,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Warnings);
