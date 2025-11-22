using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Updates;

[SupportedOSPlatform("windows")]
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

    private const int OperationResultNotStarted = 0;
    private const int OperationResultInProgress = 1;
    private const int OperationResultSucceeded = 2;
    private const int OperationResultSucceededWithErrors = 3;
    private const int OperationResultFailed = 4;
    private const int OperationResultAborted = 5;

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
                Array.Empty<string>(),
                Array.Empty<DriverUpdateSkipSummary>());
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
        var skipSummaries = MapSkipSummaries(payload?.SkipSummaries);

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
            payload?.SkipDetails is null ? Array.Empty<string>() : NormalizeWarnings(payload.SkipDetails),
            skipSummaries);
    }

    public async Task<DriverUpdateInstallResult> InstallUpdatesAsync(IReadOnlyCollection<DriverUpdateInstallRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests is null)
        {
            throw new ArgumentNullException(nameof(requests));
        }

        if (requests.Count == 0)
        {
            throw new ArgumentException("At least one driver update must be specified.", nameof(requests));
        }

        var normalized = requests
            .Where(static request => !string.IsNullOrWhiteSpace(request.UpdateId))
            .Select(static request => request with { UpdateId = request.UpdateId.Trim() })
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException("Requested updates must include a valid UpdateId.", nameof(requests));
        }

        return await Task.Run(() => InstallUpdatesInternal(normalized, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task<DriverMaintenanceResult> ReinstallDriverAsync(string? infReference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(infReference))
        {
            throw new ArgumentException("A valid INF reference is required.", nameof(infReference));
        }

        var resolvedPath = ResolveInfReference(infReference);
        var plans = new[]
        {
            new[] { "/add-driver", resolvedPath, "/install" },
            new[] { "/add-driver", resolvedPath }
        };

        return await RunPnPUtilAsync(resolvedPath, "Reinstall driver", plans, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DriverMaintenanceResult> RollbackDriverAsync(string? infReference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(infReference))
        {
            throw new ArgumentException("A valid INF reference is required.", nameof(infReference));
        }

        var resolvedPath = ResolveInfReference(infReference);
        var plans = new[]
        {
            new[] { "/delete-driver", resolvedPath, "/uninstall", "/force" },
            new[] { "/delete-driver", resolvedPath, "/force" }
        };

        return await RunPnPUtilAsync(resolvedPath, "Rollback driver", plans, cancellationToken).ConfigureAwait(false);
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
            var normalizedVendor = NormalizeKey(entry.NormalizedVendor) ?? NormalizeKey(manufacturer);
            var normalizedDriverClass = NormalizeKey(entry.NormalizedDriverClass) ?? NormalizeKey(driverClass);
            var badgeHints = MapBadgeHints(entry.BadgeHints, manufacturer, driverClass, isOptional, normalizedVendor, normalizedDriverClass);

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
        comparison,
        normalizedVendor,
        normalizedDriverClass,
        badgeHints));
        }

        return items
            .OrderByDescending(static item => item.Status == DriverUpdateStatus.UpdateAvailable ? 1 : 0)
            .ThenBy(static item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DriverUpdateBadgeHints MapBadgeHints(DriverBadgeHintsJson? metadata, string? manufacturer, string? driverClass, bool isOptional, string? normalizedVendor, string? normalizedDriverClass)
    {
        var availabilityState = Normalize(metadata?.Availability?.State) ?? "Unknown";
        var availabilityDetail = Normalize(metadata?.Availability?.Detail);

        var downgradeRisk = metadata?.DowngradeRisk?.IsRisk ?? string.Equals(availabilityState, nameof(VersionComparisonStatus.PotentialDowngrade), StringComparison.OrdinalIgnoreCase);
        var downgradeDetail = Normalize(metadata?.DowngradeRisk?.Detail);

        var vendorName = Normalize(metadata?.Vendor?.Name) ?? manufacturer;
        var vendorNormalized = NormalizeKey(metadata?.Vendor?.Normalized) ?? normalizedVendor;

        var driverClassName = Normalize(metadata?.DriverClass?.Name) ?? driverClass;
        var driverClassNormalized = NormalizeKey(metadata?.DriverClass?.Normalized) ?? normalizedDriverClass;

        var optionalLabel = Normalize(metadata?.Optional?.Label) ?? (isOptional ? "Optional" : "Recommended");
        var optionalFlag = metadata?.Optional?.IsOptional ?? isOptional;

        return new DriverUpdateBadgeHints(
            availabilityState,
            availabilityDetail,
            downgradeRisk,
            downgradeDetail,
            vendorName,
            vendorNormalized,
            driverClassName,
            driverClassNormalized,
            optionalFlag,
            optionalLabel);
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

        var items = new Dictionary<string, InstalledDriverInfo>(StringComparer.OrdinalIgnoreCase);

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

            var info = new InstalledDriverInfo(
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
                status);

            var key = BuildInstalledDriverKey(entry, info);

            if (items.TryGetValue(key, out var existing))
            {
                items[key] = MergeInstalledDriverInfo(existing, info);
            }
            else
            {
                items[key] = info;
            }
        }

        return items.Values
            .OrderBy(static item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.DriverVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<DriverUpdateSkipSummary> MapSkipSummaries(IEnumerable<DriverSkipSummaryJson>? entries)
    {
        if (entries is null)
        {
            return Array.Empty<DriverUpdateSkipSummary>();
        }

        var results = new List<DriverUpdateSkipSummary>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var title = Normalize(entry.Title) ?? Normalize(entry.DeviceName) ?? "Driver update";
            var deviceName = Normalize(entry.DeviceName);
            var manufacturer = Normalize(entry.Manufacturer);
            var normalizedVendor = NormalizeKey(entry.NormalizedVendor) ?? NormalizeKey(manufacturer);
            var driverClass = Normalize(entry.DriverClass);
            var normalizedDriverClass = NormalizeKey(entry.NormalizedDriverClass) ?? NormalizeKey(driverClass);
            var reason = Normalize(entry.Reason) ?? "Filtered by policy.";
            var reasonCode = Normalize(entry.ReasonCode) ?? "Unknown";
            var updateId = Normalize(entry.UpdateId);

            results.Add(new DriverUpdateSkipSummary(
                title,
                deviceName,
                manufacturer,
                normalizedVendor,
                driverClass,
                normalizedDriverClass,
                entry.IsOptional ?? false,
                reason,
                reasonCode,
                updateId));
        }

        return results;
    }

    private static DriverUpdateInstallResult InstallUpdatesInternal(IReadOnlyCollection<DriverUpdateInstallRequest> requests, CancellationToken cancellationToken)
    {
        dynamic? session = null;
        dynamic? searcher = null;
        dynamic? downloader = null;
        dynamic? installer = null;
        dynamic? selectedUpdates = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            session = CreateComObject("Microsoft.Update.Session");
            searcher = session.CreateUpdateSearcher();
            searcher.Online = true;

            dynamic? searchResult = searcher.Search("IsInstalled=0 and Type='Driver'");
            dynamic? availableUpdates = searchResult?.Updates;
            var availableCount = GetInt32(availableUpdates?.Count);

            if (availableUpdates is null || availableCount == 0)
            {
                return BuildInstallError("Windows Update did not return any driver updates to install.");
            }

            dynamic availableCollection = availableUpdates!;

            var requested = requests
                .GroupBy(static request => request.UpdateId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

            selectedUpdates = CreateComObject("Microsoft.Update.UpdateColl");
            var skipped = new List<string>();

            for (var i = 0; i < availableCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dynamic? update = availableCollection.Item(i);
                if (update is null || update.Identity is null)
                {
                    continue;
                }

                var updateId = update.Identity.UpdateID as string;
                if (string.IsNullOrWhiteSpace(updateId))
                {
                    continue;
                }

                if (requested.TryGetValue(updateId, out var request))
                {
                    selectedUpdates.Add(update);
                    requested.Remove(updateId);
                }
            }

            var selectedCount = GetInt32(selectedUpdates?.Count);
            if (selectedCount == 0)
            {
                return BuildInstallError("None of the requested driver updates are currently offered by Windows Update.");
            }

            if (requested.Count > 0)
            {
                skipped.AddRange(requested.Values.Select(static value => value.Title ?? value.UpdateId));
            }

            downloader = session.CreateUpdateDownloader();
            downloader.Updates = selectedUpdates;
            dynamic? downloadResult = downloader.Download();
            var downloadIssues = CollectDownloadIssues(downloadResult);

            installer = session.CreateUpdateInstaller();
            installer.Updates = selectedUpdates;

            dynamic? installResult = installer.Install();
            var summaries = BuildInstallSummaries(selectedUpdates, installResult);

            var primaryResult = MapOperationResult(GetOperationResultCode(installResult?.ResultCode));
            var succeeded = primaryResult is DriverUpdateInstallState.Installed or DriverUpdateInstallState.PartiallyInstalled;
            var messages = new List<string>();
            messages.AddRange(downloadIssues);

            if (skipped.Count > 0)
            {
                messages.Add($"Skipped {skipped.Count} request(s) that were no longer offered: {string.Join(", ", skipped)}.");
            }

            if (installResult is not null && !succeeded)
            {
                messages.Add($"Windows Update installer returned {DescribeResultCode(primaryResult)} ({FormatHResult(installResult.HResult)}).");
            }

            return new DriverUpdateInstallResult(
                succeeded,
                summaries,
                GetBoolean(installResult?.RebootRequired),
                NormalizeWarnings(messages));
        }
        finally
        {
            SafeRelease(installer);
            SafeRelease(downloader);
            SafeRelease(selectedUpdates);
            SafeRelease(searcher);
            SafeRelease(session);
        }
    }

    private static DriverUpdateInstallResult BuildInstallError(string message)
    {
        var payload = string.IsNullOrWhiteSpace(message)
            ? Array.Empty<string>()
            : new[] { message.Trim() };

        return new DriverUpdateInstallResult(false, Array.Empty<DriverUpdateInstallSummary>(), false, payload);
    }

    private static IReadOnlyList<string> CollectDownloadIssues(dynamic? result)
    {
        if (result is null)
        {
            return Array.Empty<string>();
        }

        var issues = new List<string>();

        var aggregateState = MapOperationResult(GetOperationResultCode(result.ResultCode));
        if (aggregateState == DriverUpdateInstallState.PartiallyInstalled)
        {
            issues.Add("One or more driver packages downloaded with warnings.");
        }

        dynamic? updates = result.Updates;
        var count = GetInt32(updates?.Count);
        if (count == 0)
        {
            return NormalizeWarnings(issues);
        }

        if (updates is null)
        {
            return NormalizeWarnings(issues);
        }

        dynamic updatesCollection = updates!;

        for (var i = 0; i < count; i++)
        {
            dynamic? update = updatesCollection.Item(i);
            if (update is null)
            {
                continue;
            }

            dynamic? perUpdate = result.GetUpdateResult(i);
            var perState = MapOperationResult(GetOperationResultCode(perUpdate?.ResultCode));
            if (perState == DriverUpdateInstallState.Installed || perState == DriverUpdateInstallState.Unknown)
            {
                continue;
            }

            var title = update.Title as string;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = update.Identity?.UpdateID as string ?? $"Update {i + 1}";
            }

            issues.Add($"{title}: download {DescribeResultCode(perState)} ({FormatHResult(perUpdate?.HResult)}).");
        }

        return NormalizeWarnings(issues);
    }

    private static IReadOnlyList<DriverUpdateInstallSummary> BuildInstallSummaries(dynamic? updates, dynamic? result)
    {
        if (updates is null || result is null)
        {
            return Array.Empty<DriverUpdateInstallSummary>();
        }

        var count = GetInt32(updates.Count);
        if (count == 0)
        {
            return Array.Empty<DriverUpdateInstallSummary>();
        }

        dynamic updatesCollection = updates!;

        var summaries = new List<DriverUpdateInstallSummary>(count);

        for (var i = 0; i < count; i++)
        {
            dynamic? update = updatesCollection.Item(i);
            if (update is null)
            {
                continue;
            }

            dynamic? perUpdate = result.GetUpdateResult(i);
            var state = MapOperationResult(GetOperationResultCode(perUpdate?.ResultCode));
            var updateId = update.Identity?.UpdateID as string ?? $"Update-{i + 1}";
            var rawTitle = update.Title as string;
            var title = string.IsNullOrWhiteSpace(rawTitle) ? updateId : rawTitle!;
            var hResultCode = perUpdate?.HResult ?? result.HResult;
            var detail = perUpdate is null ? null : $"{DescribeResultCode(state)} ({FormatHResult(hResultCode)})";

            summaries.Add(new DriverUpdateInstallSummary(updateId, title, state, GetInt32(hResultCode), detail));
        }

        return summaries;
    }

    private static DriverUpdateInstallState MapOperationResult(int? code)
    {
        return code switch
        {
            OperationResultNotStarted => DriverUpdateInstallState.Skipped,
            OperationResultInProgress => DriverUpdateInstallState.InProgress,
            OperationResultSucceeded => DriverUpdateInstallState.Installed,
            OperationResultSucceededWithErrors => DriverUpdateInstallState.PartiallyInstalled,
            OperationResultFailed => DriverUpdateInstallState.Failed,
            OperationResultAborted => DriverUpdateInstallState.Aborted,
            _ => DriverUpdateInstallState.Unknown
        };
    }

    private static int? GetOperationResultCode(dynamic? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeResultCode(DriverUpdateInstallState state)
    {
        return state switch
        {
            DriverUpdateInstallState.Installed => "Installed",
            DriverUpdateInstallState.PartiallyInstalled => "Installed with warnings",
            DriverUpdateInstallState.InProgress => "In progress",
            DriverUpdateInstallState.Failed => "Failed",
            DriverUpdateInstallState.Aborted => "Aborted",
            DriverUpdateInstallState.Skipped => "Not started",
            _ => "Unknown"
        };
    }

    private static string FormatHResult(dynamic? value)
    {
        var code = GetInt32(value, 0);
        return $"0x{code:X8}";
    }

    private static dynamic CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true);
        if (type is null)
        {
            throw new InvalidOperationException($"Unable to locate COM type '{progId}'.");
        }

        var instance = Activator.CreateInstance(type);
        if (instance is null)
        {
            throw new InvalidOperationException($"Failed to create COM object '{progId}'.");
        }

        return instance;
    }

    private static int GetInt32(dynamic? value, int defaultValue = 0)
    {
        if (value is null)
        {
            return defaultValue;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool GetBoolean(dynamic? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveInfReference(string infReference)
    {
        var trimmed = infReference.Trim();
        if (trimmed.Length == 0)
        {
            return infReference;
        }

        if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
        {
            return trimmed;
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            var candidate = Path.Combine(windowsDirectory, "INF", trimmed);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return trimmed;
    }

    private static async Task<DriverMaintenanceResult> RunPnPUtilAsync(string infPath, string operation, IReadOnlyList<string[]> commandPlans, CancellationToken cancellationToken)
    {
        if (commandPlans is null || commandPlans.Count == 0)
        {
            throw new ArgumentException("At least one pnputil command plan is required.", nameof(commandPlans));
        }

        var output = new List<string>();
        var usedFallback = false;

        foreach (var plan in commandPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (exitCode, lines) = await ExecuteProcessAsync("pnputil.exe", plan, cancellationToken).ConfigureAwait(false);
            output.AddRange(lines);

            if (exitCode == 0)
            {
                return new DriverMaintenanceResult(true, operation, infPath, usedFallback, NormalizeWarnings(output));
            }

            usedFallback = true;
        }

        output.Add($"{operation} failed for {infPath}. See pnputil output for details.");
        return new DriverMaintenanceResult(false, operation, infPath, usedFallback, NormalizeWarnings(output));
    }

    private static async Task<(int ExitCode, IReadOnlyList<string> Output)> ExecuteProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdOutTask.ConfigureAwait(false);
        var stderr = await stdErrTask.ConfigureAwait(false);

        var lines = SplitProcessOutput(stdout, stderr);
        return (process.ExitCode, lines);
    }

    private static IReadOnlyList<string> SplitProcessOutput(string? stdout, string? stderr)
    {
        var buffer = new List<string>();

        static void Append(string? content, ICollection<string> sink)
        {
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            using var reader = new StringReader(content);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.TrimEnd();
                if (trimmed.Length > 0)
                {
                    sink.Add(trimmed);
                }
            }
        }

        Append(stdout, buffer);
        Append(stderr, buffer);

        return buffer.Count == 0 ? Array.Empty<string>() : buffer;
    }

    private static void SafeRelease(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // Ignore release failures.
        }
    }

    private static string BuildInstalledDriverKey(InstalledDriverJson entry, InstalledDriverInfo info)
    {
        var normalizedDeviceId = NormalizeDeviceIdentity(entry.DeviceId);
        if (normalizedDeviceId is not null)
        {
            return normalizedDeviceId;
        }

        // Use stable identifying fields for deduplication. Dates are excluded because
        // install/driver timestamps may vary across records for the same logical driver
        // and cause spurious duplicates. Prefer device name + provider + manufacturer +
        // driver version + inf name + normalized hardware ids.

        var deviceName = Normalize(entry.DeviceName) ?? Normalize(info.DeviceName) ?? "*";
        var provider = Normalize(entry.Provider) ?? info.Provider ?? "*";
        var manufacturer = Normalize(entry.Manufacturer) ?? info.Manufacturer ?? "*";
        var driverVersion = Normalize(entry.DriverVersion) ?? info.DriverVersion ?? "*";
        var infName = Normalize(entry.InfName) ?? info.InfName ?? "*";

        var canonicalHardware = NormalizeHardwareIdentifiers(entry.HardwareIds, info.HardwareIds);
        var hardwarePart = canonicalHardware.Length == 0
            ? "*"
            : string.Join("|", canonicalHardware.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

        var fallbackKey = string.Join("||", new[] { deviceName, provider, manufacturer, driverVersion, infName, hardwarePart }
            .Select(static part => string.IsNullOrWhiteSpace(part) ? "*" : part.Trim())
            .Select(static part => part.ToUpperInvariant()));

        return fallbackKey;
    }

    private static string? NormalizeDeviceIdentity(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var trimmed = deviceId.Trim();

        var segments = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var bus = segments[0].ToUpperInvariant();

        if (segments.Length == 1)
        {
            return bus;
        }

        var identifier = CanonicalizeIdentitySegment(segments[1]);
        if (identifier.Length == 0)
        {
            return bus;
        }

        return string.Concat(bus, "\\", identifier);
    }

    private static string[] NormalizeHardwareIdentifiers(IEnumerable<string?>? rawHardwareIds, IReadOnlyList<string> fallback)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rawHardwareIds is not null)
        {
            foreach (var entry in rawHardwareIds)
            {
                var canonical = NormalizeDeviceIdentity(entry);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    normalized.Add(canonical!);
                }
            }
        }

        if (normalized.Count == 0 && fallback.Count > 0)
        {
            foreach (var entry in fallback)
            {
                var canonical = NormalizeDeviceIdentity(entry);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    normalized.Add(canonical!);
                }
            }
        }

        return normalized.Count == 0 ? Array.Empty<string>() : normalized.ToArray();
    }

    private static string CanonicalizeIdentitySegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var tokens = segment
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.ToUpperInvariant())
            .Where(static token => token.Length > 0)
            .Where(static token => !ShouldSkipIdentityToken(token))
            .ToList();

        if (tokens.Count == 0)
        {
            return segment.ToUpperInvariant();
        }

        return string.Join('&', tokens);
    }

    private static bool ShouldSkipIdentityToken(string token)
    {
        if (token.StartsWith("REV_", StringComparison.Ordinal) ||
            token.StartsWith("SUBSYS_", StringComparison.Ordinal) ||
            token.StartsWith("SUBVID_", StringComparison.Ordinal) ||
            token.StartsWith("SUBDEV_", StringComparison.Ordinal) ||
            token.StartsWith("CC_", StringComparison.Ordinal) ||
            token.StartsWith("MI_", StringComparison.Ordinal) ||
            token.StartsWith("FUNC_", StringComparison.Ordinal) ||
            token.StartsWith("FN_", StringComparison.Ordinal) ||
            token.StartsWith("UID_", StringComparison.Ordinal) ||
            token.StartsWith("INSTANCEID_", StringComparison.Ordinal) ||
            token.StartsWith("RID_", StringComparison.Ordinal) ||
            token.StartsWith("SERIAL", StringComparison.Ordinal) ||
            token.StartsWith("COL", StringComparison.Ordinal))
        {
            return true;
        }

        // Skip Windows-generated container identifiers that look like GUIDs or opaque hashes.
        if (token.Length >= 32 && token.All(static c => char.IsDigit(c) || (c >= 'A' && c <= 'F')))
        {
            return true;
        }

        return false;
    }

    private static InstalledDriverInfo MergeInstalledDriverInfo(InstalledDriverInfo existing, InstalledDriverInfo incoming)
    {
        var deviceName = ReplacePlaceholder(existing.DeviceName, incoming.DeviceName, "Unknown device");
        var manufacturer = PreferString(existing.Manufacturer, incoming.Manufacturer);
        var provider = PreferString(existing.Provider, incoming.Provider);
        var driverVersion = PreferString(existing.DriverVersion, incoming.DriverVersion);
        var driverDate = existing.DriverDate ?? incoming.DriverDate;
        var installDate = existing.InstallDate ?? incoming.InstallDate;
        var classGuid = PreferString(existing.ClassGuid, incoming.ClassGuid);
        var description = PreferString(existing.Description, incoming.Description);
        var hardwareIds = MergeHardwareIds(existing.HardwareIds, incoming.HardwareIds);
        var isSigned = existing.IsSigned ?? incoming.IsSigned;
        var infName = PreferString(existing.InfName, incoming.InfName);
        var deviceId = PreferString(existing.DeviceId, incoming.DeviceId);
        var problemCode = existing.ProblemCode ?? incoming.ProblemCode;
        var status = PreferStatus(existing.Status, incoming.Status);

        return existing with
        {
            DeviceName = deviceName,
            Manufacturer = manufacturer,
            Provider = provider,
            DriverVersion = driverVersion,
            DriverDate = driverDate,
            InstallDate = installDate,
            ClassGuid = classGuid,
            Description = description,
            HardwareIds = hardwareIds,
            IsSigned = isSigned,
            InfName = infName,
            DeviceId = deviceId,
            ProblemCode = problemCode,
            Status = status
        };
    }

    private static IReadOnlyList<string> MergeHardwareIds(IReadOnlyList<string> existing, IReadOnlyList<string> incoming)
    {
        if (existing.Count == 0)
        {
            return incoming;
        }

        if (incoming.Count == 0)
        {
            return existing;
        }

        return existing
            .Concat(incoming)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string PreferStatus(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing) || string.Equals(existing, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return incoming;
        }

        if (string.IsNullOrWhiteSpace(incoming) || string.Equals(incoming, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return existing;
    }

    private static string? PreferString(string? existing, string? candidate)
    {
        return string.IsNullOrWhiteSpace(existing) ? candidate : existing;
    }

    private static string ReplacePlaceholder(string existing, string replacement, string placeholder)
    {
        if (!string.Equals(existing, placeholder, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return string.Equals(replacement, placeholder, StringComparison.OrdinalIgnoreCase)
            ? existing
            : replacement;
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

    private static string? NormalizeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
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
        public List<DriverSkipSummaryJson>? SkipSummaries { get; set; }
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
        public DriverBadgeHintsJson? BadgeHints { get; set; }
        public string? NormalizedVendor { get; set; }
        public string? NormalizedDriverClass { get; set; }
    }

    private sealed class VersionComparisonJson
    {
        public string? Status { get; set; }
        public string? Details { get; set; }
    }

    private sealed class DriverBadgeHintsJson
    {
        public AvailabilityBadgeJson? Availability { get; set; }
        public DowngradeBadgeJson? DowngradeRisk { get; set; }
        public VendorBadgeJson? Vendor { get; set; }
        public DriverClassBadgeJson? DriverClass { get; set; }
        public OptionalBadgeJson? Optional { get; set; }
    }

    private sealed class AvailabilityBadgeJson
    {
        public string? State { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class DowngradeBadgeJson
    {
        public bool? IsRisk { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class VendorBadgeJson
    {
        public string? Name { get; set; }
        public string? Normalized { get; set; }
    }

    private sealed class DriverClassBadgeJson
    {
        public string? Name { get; set; }
        public string? Normalized { get; set; }
    }

    private sealed class OptionalBadgeJson
    {
        public bool? IsOptional { get; set; }
        public string? Label { get; set; }
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

    private sealed class DriverSkipSummaryJson
    {
        public string? Title { get; set; }
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string? NormalizedVendor { get; set; }
        public string? DriverClass { get; set; }
        public string? NormalizedDriverClass { get; set; }
        public bool? IsOptional { get; set; }
        public string? Reason { get; set; }
        public string? ReasonCode { get; set; }
        public string? UpdateId { get; set; }
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
    VersionComparisonInfo VersionComparison,
    string? NormalizedVendor,
    string? NormalizedDriverClass,
    DriverUpdateBadgeHints BadgeHints);

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
    IReadOnlyList<string> SkipDetails,
    IReadOnlyList<DriverUpdateSkipSummary> SkipSummaries);

public sealed record DriverUpdateBadgeHints(
    string AvailabilityState,
    string? AvailabilityDetail,
    bool IsDowngradeRisk,
    string? DowngradeRiskDetail,
    string? VendorName,
    string? NormalizedVendor,
    string? DriverClassName,
    string? NormalizedDriverClass,
    bool IsOptional,
    string OptionalLabel);

public sealed record DriverUpdateSkipSummary(
    string Title,
    string? DeviceName,
    string? Manufacturer,
    string? NormalizedVendor,
    string? DriverClass,
    string? NormalizedDriverClass,
    bool IsOptional,
    string Reason,
    string ReasonCode,
    string? UpdateId);

public sealed record DriverUpdateInstallRequest(string UpdateId, string? Title);

public sealed record DriverUpdateInstallResult(
    bool Success,
    IReadOnlyList<DriverUpdateInstallSummary> Updates,
    bool RebootRequired,
    IReadOnlyList<string> Messages);

public sealed record DriverUpdateInstallSummary(
    string UpdateId,
    string Title,
    DriverUpdateInstallState State,
    int HResult,
    string? Detail);

public enum DriverUpdateInstallState
{
    Unknown,
    Skipped,
    InProgress,
    Installed,
    PartiallyInstalled,
    Failed,
    Aborted
}

public sealed record DriverMaintenanceResult(
    bool Success,
    string Operation,
    string TargetInfPath,
    bool UsedFallbackPlan,
    IReadOnlyList<string> Messages);
