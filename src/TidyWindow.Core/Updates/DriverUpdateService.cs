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

    private static readonly IReadOnlyDictionary<int, string> DeviceProblemCodeDescriptions = new Dictionary<int, string>
    {
        { 0, "Working" },
        { 1, "Configuration required" },
        { 2, "Driver failed to load" },
        { 3, "Driver may be corrupted" },
        { 4, "Device reported a problem" },
        { 5, "Resource allocation failure" },
        { 6, "Boot configuration conflict" },
        { 7, "Cannot filter device" },
        { 8, "Driver loader missing" },
        { 9, "Hardware signaled a failure" },
        { 10, "Cannot start" },
        { 11, "Device failure" },
        { 12, "Not enough resources" },
        { 13, "Resource verification failed" },
        { 14, "Restart required" },
        { 15, "Driver configuration incomplete" },
        { 16, "Cannot identify required resources" },
        { 17, "Device caused a system failure" },
        { 18, "Reinstall the driver" },
        { 19, "Configuration data invalid" },
        { 20, "Conflicts with another device" },
        { 21, "Device is being removed" },
        { 22, "Disabled" },
        { 23, "System failure" },
        { 24, "Hardware missing or offline" },
        { 25, "Device reported removal" },
        { 26, "Device not ready" },
        { 27, "No valid log configuration" },
        { 28, "Driver not installed" },
        { 29, "Firmware failed to start" },
        { 30, "Incorrect driver loaded" },
        { 31, "Driver not working properly" }
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
            return new DriverUpdateScanResult(
                Array.Empty<DriverUpdateInfo>(),
                DateTimeOffset.UtcNow,
                NormalizeWarnings(result.Errors),
                Array.Empty<InstalledDriverInfo>(),
                null,
                0,
                0,
                Array.Empty<string>());
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
        var installedDrivers = MapInstalledDrivers(payload?.InstalledDrivers);
        var filters = MapFilters(payload?.AppliedFilters);
        var generatedAt = ResolveTimestamp(payload?.GeneratedAtUtc) ?? DateTimeOffset.UtcNow;

        var warningsBuffer = new List<string>(NormalizeWarnings(result.Errors));
        if ((payload?.SkippedOptional ?? 0) > 0 && !(payload?.IncludeOptional ?? false))
        {
            warningsBuffer.Add($"Skipped {payload!.SkippedOptional} optional update(s). Enable optional scans to include them.");
        }

        if ((payload?.SkippedByFilters ?? 0) > 0)
        {
            warningsBuffer.Add($"{payload!.SkippedByFilters} update(s) were filtered out by driver class or vendor rules.");
        }

        var warnings = NormalizeWarnings(warningsBuffer);

        return new DriverUpdateScanResult(
            updates,
            generatedAt,
            warnings,
            installedDrivers,
            filters,
            payload?.SkippedOptional ?? 0,
            payload?.SkippedByFilters ?? 0,
            payload?.SkipDetails is null ? Array.Empty<string>() : NormalizeWarnings(payload.SkipDetails));
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
            var driverClass = Normalize(entry.DriverClass);
            var classification = Normalize(entry.Classification);
            var severity = Normalize(entry.Severity);
            var updateId = Normalize(entry.UpdateId);
            var revisionNumber = entry.RevisionNumber;
            var installedInfPath = Normalize(entry.InstalledInfPath);
            var installedManufacturer = Normalize(entry.InstalledManufacturer);
            var comparison = MapVersionComparison(entry.VersionComparison);

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
        description,
        driverClass,
        classification,
        severity,
        updateId,
        revisionNumber,
        installedInfPath,
        installedManufacturer,
        comparison));
        }

        return items
            .OrderByDescending(static item => item.Status == DriverUpdateStatus.UpdateAvailable ? 1 : 0)
            .ThenBy(static item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static VersionComparisonInfo MapVersionComparison(VersionComparisonJson? metadata)
    {
        var details = Normalize(metadata?.Details);

        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Status))
        {
            return new VersionComparisonInfo(VersionComparisonStatus.Unknown, details);
        }

        if (Enum.TryParse<VersionComparisonStatus>(metadata.Status, true, out var parsed))
        {
            return new VersionComparisonInfo(parsed, details);
        }

        return new VersionComparisonInfo(VersionComparisonStatus.Unknown, details);
    }

    private static IReadOnlyList<InstalledDriverInfo> MapInstalledDrivers(IEnumerable<InstalledDriverJson>? entries)
    {
        if (entries is null)
        {
            return Array.Empty<InstalledDriverInfo>();
        }

        var items = new List<InstalledDriverInfo>();

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var hardwareIds = NormalizeStringArray(entry.HardwareIds, StringComparer.OrdinalIgnoreCase);
            var driverDate = ResolveTimestamp(entry.DriverDate);
            var installDate = ResolveTimestamp(entry.InstallDate);
            var status = ResolveDriverStatus(entry.Status, entry.ProblemCode, entry.Signed);

            items.Add(new InstalledDriverInfo(
                Normalize(entry.DeviceName) ?? "Unknown device",
                Normalize(entry.Manufacturer),
                Normalize(entry.Provider),
                Normalize(entry.DriverVersion),
                driverDate,
                installDate,
                Normalize(entry.ClassGuid),
                Normalize(entry.DriverDescription),
                hardwareIds,
                entry.Signed,
                Normalize(entry.InfName),
                Normalize(entry.DeviceId),
                entry.ProblemCode,
                status));
        }

        return items
            .OrderBy(static item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.DriverVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveDriverStatus(string? rawStatus, int? problemCode, bool? isSigned)
    {
        var normalized = Normalize(rawStatus);

        if (!string.IsNullOrWhiteSpace(normalized) && !normalized.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                return "Working";
            }

            if (normalized.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                return "Device reported a problem";
            }

            if (normalized.Equals("Degraded", StringComparison.OrdinalIgnoreCase))
            {
                return "Running with reduced functionality";
            }

            if (normalized.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase))
            {
                return "Predictive failure reported";
            }

            if (normalized.Equals("Starting", StringComparison.OrdinalIgnoreCase))
            {
                return "Starting";
            }

            if (normalized.Equals("Stopping", StringComparison.OrdinalIgnoreCase))
            {
                return "Stopping";
            }

            return normalized;
        }

        if (problemCode is int code)
        {
            if (DeviceProblemCodeDescriptions.TryGetValue(code, out var description))
            {
                return description;
            }

            return $"Problem code {code}";
        }

        if (isSigned is false)
        {
            return "Unsigned";
        }

        if (isSigned is true)
        {
            return "Working";
        }

        return "Not reported";
    }

    private static DriverFilterSummary? MapFilters(DriverFilterMetadataJson? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var include = NormalizeStringArray(metadata.IncludeDriverClasses, StringComparer.OrdinalIgnoreCase);
        var exclude = NormalizeStringArray(metadata.ExcludeDriverClasses, StringComparer.OrdinalIgnoreCase);
        var allow = NormalizeStringArray(metadata.AllowVendors, StringComparer.OrdinalIgnoreCase);
        var block = NormalizeStringArray(metadata.BlockVendors, StringComparer.OrdinalIgnoreCase);

        if (include.Length == 0 && exclude.Length == 0 && allow.Length == 0 && block.Length == 0)
        {
            return null;
        }

        return new DriverFilterSummary(include, exclude, allow, block);
    }

    private static string[] NormalizeStringArray(IEnumerable<string?>? values, IEqualityComparer<string>? comparer)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        comparer ??= StringComparer.Ordinal;

        return values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(comparer)
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

    private static string[] NormalizeWarnings(IEnumerable<string?> warnings)
    {
        if (warnings is null)
        {
            return Array.Empty<string>();
        }

        return warnings
            .Select(static warning => warning?.Trim())
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .Select(static warning => warning!)
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
        public string? SchemaVersion { get; set; }
        public string? GeneratedAtUtc { get; set; }
        public bool? IncludeOptional { get; set; }
        public int? SkippedOptional { get; set; }
        public int? SkippedByFilters { get; set; }
        public string[]? SkipDetails { get; set; }
        public DriverFilterMetadataJson? AppliedFilters { get; set; }
        public List<DriverUpdateJson>? Updates { get; set; }
        public List<InstalledDriverJson>? InstalledDrivers { get; set; }
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
        public string? DriverClass { get; set; }
        public string? Classification { get; set; }
        public string? Severity { get; set; }
        public string? UpdateId { get; set; }
        public int? RevisionNumber { get; set; }
        public string? InstalledInfPath { get; set; }
        public string? InstalledManufacturer { get; set; }
        public VersionComparisonJson? VersionComparison { get; set; }
    }

    private sealed class VersionComparisonJson
    {
        public string? Status { get; set; }
        public string? Details { get; set; }
    }

    private sealed class DriverFilterMetadataJson
    {
        public string[]? IncludeDriverClasses { get; set; }
        public string[]? ExcludeDriverClasses { get; set; }
        public string[]? AllowVendors { get; set; }
        public string[]? BlockVendors { get; set; }
    }

    private sealed class InstalledDriverJson
    {
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string? Provider { get; set; }
        public string? DriverVersion { get; set; }
        public string? DriverDate { get; set; }
        public string? InstallDate { get; set; }
        public string? ClassGuid { get; set; }
        public string? DriverDescription { get; set; }
        public string[]? HardwareIds { get; set; }
        public bool? Signed { get; set; }
        public string? InfName { get; set; }
        public string? DeviceId { get; set; }
        public int? ProblemCode { get; set; }
        public string? Status { get; set; }
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
    string? Description,
    string? DriverClass,
    string? Classification,
    string? Severity,
    string? UpdateId,
    int? RevisionNumber,
    string? InstalledInfPath,
    string? InstalledManufacturer,
    VersionComparisonInfo VersionComparison);

public sealed record VersionComparisonInfo(VersionComparisonStatus Status, string? Details);

public enum VersionComparisonStatus
{
    Unknown,
    UpdateAvailable,
    PotentialDowngrade,
    Equal
}

public sealed record InstalledDriverInfo(
    string DeviceName,
    string? Manufacturer,
    string? Provider,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    DateTimeOffset? InstallDate,
    string? ClassGuid,
    string? Description,
    IReadOnlyList<string> HardwareIds,
    bool? IsSigned,
    string? InfName,
    string? DeviceId,
    int? ProblemCode,
    string Status);

public sealed record DriverFilterSummary(
    IReadOnlyList<string> IncludeDriverClasses,
    IReadOnlyList<string> ExcludeDriverClasses,
    IReadOnlyList<string> AllowVendors,
    IReadOnlyList<string> BlockVendors);

public sealed record DriverUpdateScanResult(
    IReadOnlyList<DriverUpdateInfo> Updates,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<InstalledDriverInfo> InstalledDrivers,
    DriverFilterSummary? Filters,
    int SkippedOptional,
    int SkippedByFilters,
    IReadOnlyList<string> SkipDetails);
