using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.Services;

/// <summary>
/// Resolves catalog service identifiers to actual Windows services installed on the local machine.
/// </summary>
public sealed class ServiceResolver
{
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, ServiceInfo> _byServiceName = new Dictionary<string, ServiceInfo>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ServiceInfo> _byDisplayName = new Dictionary<string, ServiceInfo>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRefreshedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public ServiceResolution Resolve(string? serviceIdentifier, string? displayNameFallback)
    {
        var result = ResolveMany(serviceIdentifier, displayNameFallback);
        if (result.Status != ServiceResolutionStatus.Available)
        {
            return ServiceResolution.FromMany(result);
        }

        var first = result.Candidates.First();
        return ServiceResolution.Available(first.ServiceName, first.MatchedByDisplayName);
    }

    public ServiceResolutionMany ResolveMany(string? serviceIdentifier, string? displayNameFallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceResolutionMany.NotInstalled("Windows services are unavailable on this OS.");
        }

        EnsureIndex();

        var isPattern = ContainsWildcards(serviceIdentifier);
        var normalizedId = string.IsNullOrWhiteSpace(serviceIdentifier)
            ? null
            : ProcessCatalogEntry.NormalizeServiceIdentifier(serviceIdentifier);

        if (!isPattern && normalizedId is null)
        {
            // Try fallback to display name for strict resolution
            var displayKey = NormalizeDisplayName(displayNameFallback);
            if (!string.IsNullOrWhiteSpace(displayKey) && _byDisplayName.TryGetValue(displayKey, out var infoByDisplay))
            {
                return ServiceResolutionMany.Available(new[] { new ServiceCandidate(infoByDisplay.ServiceName, true) });
            }

            return ServiceResolutionMany.InvalidName("Service identifier contains invalid characters or is missing.");
        }

        if (!isPattern)
        {
            if (normalizedId is not null && _byServiceName.TryGetValue(normalizedId, out var infoByName))
            {
                return ServiceResolutionMany.Available(new[] { new ServiceCandidate(infoByName.ServiceName, false) });
            }

            var displayFallback = NormalizeDisplayName(displayNameFallback ?? serviceIdentifier);
            if (!string.IsNullOrWhiteSpace(displayFallback) && _byDisplayName.TryGetValue(displayFallback, out var infoByDisplayName))
            {
                return ServiceResolutionMany.Available(new[] { new ServiceCandidate(infoByDisplayName.ServiceName, true) });
            }

            return ServiceResolutionMany.NotInstalled("Service not installed on this PC.");
        }

        // Pattern path: expand wildcards across service name and display name
        var pattern = serviceIdentifier ?? displayNameFallback;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return ServiceResolutionMany.InvalidName("No service identifier provided.");
        }

        var regex = BuildWildcardRegex(pattern);
        var candidates = new List<ServiceCandidate>();

        foreach (var pair in _byServiceName)
        {
            if (regex.IsMatch(pair.Key))
            {
                candidates.Add(new ServiceCandidate(pair.Value.ServiceName, false));
            }
        }

        foreach (var pair in _byDisplayName)
        {
            if (regex.IsMatch(pair.Key))
            {
                var info = pair.Value;
                if (candidates.All(c => !string.Equals(c.ServiceName, info.ServiceName, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(new ServiceCandidate(info.ServiceName, true));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return ServiceResolutionMany.NotInstalled("No services matched this pattern on this PC.");
        }

        return ServiceResolutionMany.Available(candidates);
    }

    private void EnsureIndex()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastRefreshedUtc) < CacheDuration && _byServiceName.Count > 0)
            {
                return;
            }

            RefreshIndexNoLock();
        }
    }

    private void RefreshIndexNoLock()
    {
        var services = ServiceController.GetServices();
        try
        {
            var byServiceName = new Dictionary<string, ServiceInfo>(services.Length, StringComparer.OrdinalIgnoreCase);
            var byDisplayName = new Dictionary<string, ServiceInfo>(services.Length, StringComparer.OrdinalIgnoreCase);

            foreach (var service in services)
            {
                var info = new ServiceInfo(service.ServiceName, service.DisplayName);

                if (!byServiceName.ContainsKey(info.ServiceName))
                {
                    byServiceName[info.ServiceName] = info;
                }

                var displayKey = NormalizeDisplayName(info.DisplayName);
                if (!string.IsNullOrWhiteSpace(displayKey) && !byDisplayName.ContainsKey(displayKey))
                {
                    byDisplayName[displayKey] = info;
                }
            }

            _byServiceName = byServiceName;
            _byDisplayName = byDisplayName;
            _lastRefreshedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            foreach (var service in services)
            {
                try
                {
                    service.Close();
                    service.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static string NormalizeDisplayName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static bool ContainsWildcards(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.IndexOf('*') >= 0 || value.IndexOf('?') >= 0;
    }

    private static System.Text.RegularExpressions.Regex BuildWildcardRegex(string pattern)
    {
        var escaped = System.Text.RegularExpressions.Regex.Escape(pattern.Trim());
        var regexPattern = "^" + escaped.Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new System.Text.RegularExpressions.Regex(regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);
    }

    private sealed record ServiceInfo(string ServiceName, string DisplayName);
}

public enum ServiceResolutionStatus
{
    Available,
    NotInstalled,
    InvalidName
}

public sealed record ServiceResolution(ServiceResolutionStatus Status, string? ServiceName, bool MatchedByDisplayName, string? Reason)
{
    public static ServiceResolution Available(string serviceName, bool matchedByDisplayName) => new(ServiceResolutionStatus.Available, serviceName, matchedByDisplayName, null);

    public static ServiceResolution NotInstalled(string reason) => new(ServiceResolutionStatus.NotInstalled, null, false, string.IsNullOrWhiteSpace(reason) ? "Service not installed on this PC." : reason.Trim());

    public static ServiceResolution InvalidName(string reason) => new(ServiceResolutionStatus.InvalidName, null, false, string.IsNullOrWhiteSpace(reason) ? "Service identifier is invalid." : reason.Trim());

    internal static ServiceResolution FromMany(ServiceResolutionMany result)
    {
        return result.Status switch
        {
            ServiceResolutionStatus.Available when result.Candidates.Count > 0 => Available(result.Candidates[0].ServiceName, result.Candidates[0].MatchedByDisplayName),
            ServiceResolutionStatus.NotInstalled => NotInstalled(result.Reason ?? "Service not installed on this PC."),
            ServiceResolutionStatus.InvalidName => InvalidName(result.Reason ?? "Service identifier is invalid."),
            _ => InvalidName("Unable to resolve service.")
        };
    }
}

public sealed record ServiceCandidate(string ServiceName, bool MatchedByDisplayName);

public sealed record ServiceResolutionMany(ServiceResolutionStatus Status, IReadOnlyList<ServiceCandidate> Candidates, string? Reason)
{
    public static ServiceResolutionMany Available(IReadOnlyList<ServiceCandidate> candidates) => new(ServiceResolutionStatus.Available, candidates, null);

    public static ServiceResolutionMany NotInstalled(string reason) => new(ServiceResolutionStatus.NotInstalled, Array.Empty<ServiceCandidate>(), string.IsNullOrWhiteSpace(reason) ? "Service not installed on this PC." : reason.Trim());

    public static ServiceResolutionMany InvalidName(string reason) => new(ServiceResolutionStatus.InvalidName, Array.Empty<ServiceCandidate>(), string.IsNullOrWhiteSpace(reason) ? "Service identifier is invalid." : reason.Trim());
}
