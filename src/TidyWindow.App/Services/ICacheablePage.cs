using System;
using System.Collections.Generic;
using TidyWindow.App.Views;

namespace TidyWindow.App.Services;

/// <summary>
/// Describes caching preferences for a Page type.
/// </summary>
public sealed record PageCachePolicy(TimeSpan? IdleExpiration)
{
    public static PageCachePolicy KeepAlive { get; } = new((TimeSpan?)null);

    public static PageCachePolicy Sliding(TimeSpan ttl)
        => new(ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : ttl);
}

/// <summary>
/// Central registry describing which pages are safe to cache and reuse.
/// </summary>
public static class PageCacheRegistry
{
    private static readonly Dictionary<Type, PageCachePolicy> _cacheablePages = new()
    {
        { typeof(BootstrapPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(30)) },
        { typeof(CleanupPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(20)) },
        { typeof(DeepScanPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(20)) },
        { typeof(EssentialsPage), PageCachePolicy.KeepAlive },
        { typeof(InstallHubPage), PageCachePolicy.KeepAlive },
        { typeof(KnownProcessesPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(20)) },
        { typeof(StartupControllerPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(20)) },
        { typeof(LogsPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(20)) },
        { typeof(PackageMaintenancePage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(25)) },
        // PathPilot is heavier (PowerShell inventory + cached visuals); keep uncached to avoid lingering UI latency after navigation.
        { typeof(LatencyGuardPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(20)) },
        { typeof(ResetRescuePage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(25)) },
        { typeof(RegistryOptimizerPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(30)) },
        { typeof(SettingsPage), PageCachePolicy.Sliding(TimeSpan.FromMinutes(30)) }
    };

    public static IReadOnlyCollection<Type> CacheablePages => _cacheablePages.Keys;

    public static bool IsCacheable(Type? pageType) => TryGetPolicy(pageType, out _);

    public static bool TryGetPolicy(Type? pageType, out PageCachePolicy policy)
    {
        if (pageType is null)
        {
            policy = null!;
            return false;
        }

        return _cacheablePages.TryGetValue(pageType, out policy!);
    }

    public static void Register(Type pageType, PageCachePolicy policy)
    {
        if (pageType is null)
        {
            return;
        }

        _cacheablePages[pageType] = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public static void Unregister(Type pageType)
    {
        if (pageType is null)
        {
            return;
        }

        _cacheablePages.Remove(pageType);
    }
}
