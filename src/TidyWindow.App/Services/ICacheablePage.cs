using System;
using System.Collections.Generic;
using TidyWindow.App.Views;

namespace TidyWindow.App.Services;

/// <summary>
/// Central registry describing which pages are safe to cache and reuse.
/// </summary>
public static class PageCacheRegistry
{
    private static readonly HashSet<Type> _cacheablePages = new(new[]
    {
        typeof(CleanupPage),
        typeof(EssentialsPage),
        typeof(InstallHubPage),
        typeof(PackageMaintenancePage),
        typeof(RegistryOptimizerPage),
        typeof(KnownProcessesPage),
        typeof(DeepScanPage)
    });

    public static IReadOnlyCollection<Type> CacheablePages => _cacheablePages;

    public static bool IsCacheable(Type? pageType)
    {
        return pageType is not null && _cacheablePages.Contains(pageType);
    }

    public static void Register(Type pageType)
    {
        if (pageType is null)
        {
            return;
        }

        _cacheablePages.Add(pageType);
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
