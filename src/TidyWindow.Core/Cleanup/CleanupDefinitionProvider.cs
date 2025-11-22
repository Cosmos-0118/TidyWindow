using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TidyWindow.Core.Cleanup;

internal sealed class CleanupDefinitionProvider
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<CleanupTargetDefinition> GetDefinitions(bool includeDownloads, bool includeBrowserHistory)
    {
        var definitions = new List<CleanupTargetDefinition>
        {
            new CleanupTargetDefinition("Temp", "User Temp", Environment.GetEnvironmentVariable("TEMP"), "Temporary files generated for the current user."),
            new CleanupTargetDefinition("Temp", "Local AppData Temp", Combine(LocalAppData, "Temp"), "Local application temp directory for the current user."),
            new CleanupTargetDefinition("Temp", "Windows Temp", Combine(WindowsDirectory, "Temp"), "System-wide temporary files created by Windows."),
            new CleanupTargetDefinition("Temp", "Windows Prefetch", Combine(WindowsDirectory, "Prefetch"), "Prefetch hints used by Windows to speed up application launches."),

            new CleanupTargetDefinition("Cache", "Windows Update Cache", Combine(WindowsDirectory, "SoftwareDistribution", "Download"), "Cached Windows Update payloads that can be regenerated as needed."),
            new CleanupTargetDefinition("Cache", "Delivery Optimization Cache", Combine(ProgramData, "Microsoft", "Network", "Downloader"), "Delivery Optimization cache for Windows Update and Store content."),
            new CleanupTargetDefinition("Cache", "Microsoft Store Cache", Combine(LocalAppData, "Packages", "Microsoft.WindowsStore_8wekyb3d8bbwe", "LocalCache"), "Microsoft Store cached assets."),
            new CleanupTargetDefinition("Cache", "WinGet Cache", Combine(LocalAppData, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalCache"), "WinGet package metadata and cache files."),
            new CleanupTargetDefinition("Cache", "NuGet HTTP Cache", Combine(LocalAppData, "NuGet", "Cache"), "NuGet HTTP cache used by developer tooling."),
        };

        definitions.AddRange(GetEdgeCacheDefinitions());
        definitions.AddRange(GetChromeCacheDefinitions());
        definitions.AddRange(GetFirefoxCacheDefinitions());
        definitions.AddRange(GetTeamsCacheDefinitions());
        if (includeBrowserHistory)
        {
            definitions.AddRange(GetEdgeHistoryDefinitions());
            definitions.AddRange(GetChromeHistoryDefinitions());
        }
        definitions.AddRange(GetAdditionalSafeTargets());

        definitions.AddRange(new[]
        {
            new CleanupTargetDefinition("Logs", "Windows Error Reporting Queue", Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"), "Queued Windows Error Reporting crash dumps and diagnostics."),
            new CleanupTargetDefinition("Logs", "Windows Update Logs", Combine(WindowsDirectory, "Logs", "WindowsUpdate"), "Windows Update diagnostic logs."),
            new CleanupTargetDefinition("Logs", "OneDrive Logs", Combine(LocalAppData, "Microsoft", "OneDrive", "logs"), "Microsoft OneDrive sync client logs."),
            new CleanupTargetDefinition("Orphaned", "User Crash Dumps", Combine(LocalAppData, "CrashDumps"), "Application crash dump files created for troubleshooting."),
            new CleanupTargetDefinition("Orphaned", "System Crash Dumps", Combine(WindowsDirectory, "Minidump"), "System crash dump files."),
            new CleanupTargetDefinition("Orphaned", "Squirrel Installer Cache", Combine(LocalAppData, "SquirrelTemp"), "Residual setup artifacts from Squirrel-based installers."),
        });

        if (includeDownloads)
        {
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                definitions.Add(new CleanupTargetDefinition("Downloads", "User Downloads", Combine(userProfile, "Downloads"), "Files downloaded by the current user."));
            }
        }

        return definitions;
    }

    private static IEnumerable<CleanupTargetDefinition> GetEdgeHistoryDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data");
        return GetChromiumHistoryDefinitions(basePath, "Microsoft Edge");
    }

    private static IEnumerable<CleanupTargetDefinition> GetChromeHistoryDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "Google", "Chrome", "User Data");
        return GetChromiumHistoryDefinitions(basePath, "Google Chrome");
    }

    private static IEnumerable<CleanupTargetDefinition> GetChromiumHistoryDefinitions(string basePath, string browserLabel)
    {
        if (!Directory.Exists(basePath))
        {
            return Array.Empty<CleanupTargetDefinition>();
        }

        var targets = new List<CleanupTargetDefinition>();

        foreach (var profileDir in SafeEnumerateDirectories(basePath))
        {
            var profileName = Path.GetFileName(profileDir);
            if (string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            var labelPrefix = string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase)
                ? $"{browserLabel} (Default profile)"
                : $"{browserLabel} ({profileName})";

            foreach (var history in ChromiumHistoryFiles)
            {
                var candidate = Path.Combine(profileDir, history.FileName);
                var definition = TryCreateFileDefinition(
                    "History",
                    $"{labelPrefix} {history.LabelSuffix}",
                    candidate,
                    history.Notes);

                if (definition is not null)
                {
                    targets.Add(definition);
                }
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetAdditionalSafeTargets()
    {
        return new[]
        {
            new CleanupTargetDefinition("Cache", "DirectX Shader Cache", Combine(LocalAppData, "D3DSCache"), "Compiled DirectX shader cache generated by games and apps."),
            new CleanupTargetDefinition("Cache", "Windows Font Cache", Combine(LocalAppData, "FontCache"), "Font cache data regenerated automatically by Windows."),
            new CleanupTargetDefinition("Cache", "Legacy INet Cache", Combine(LocalAppData, "Microsoft", "Windows", "INetCache"), "Legacy browser/WebView cache files."),
        };
    }

    private static IEnumerable<CleanupTargetDefinition> GetEdgeCacheDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data");
        if (!Directory.Exists(basePath))
        {
            return Array.Empty<CleanupTargetDefinition>();
        }

        var targets = new List<CleanupTargetDefinition>();

        var profileDirs = SafeEnumerateDirectories(basePath).ToArray();
        foreach (var profileDir in profileDirs)
        {
            var profileName = Path.GetFileName(profileDir);
            if (string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            var labelPrefix = string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase)
                ? "Microsoft Edge (Default profile)"
                : $"Microsoft Edge ({profileName})";

            foreach (var target in EdgeSubFolders)
            {
                var candidate = Path.Combine(profileDir, target.SubPath);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                targets.Add(new CleanupTargetDefinition("Cache", $"{labelPrefix} {target.LabelSuffix}", candidate, target.Notes));
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetChromeCacheDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "Google", "Chrome", "User Data");
        if (!Directory.Exists(basePath))
        {
            return Array.Empty<CleanupTargetDefinition>();
        }

        var targets = new List<CleanupTargetDefinition>();

        var profileDirs = SafeEnumerateDirectories(basePath)
            .Where(dir => IsChromeProfile(Path.GetFileName(dir)))
            .ToArray();

        foreach (var profileDir in profileDirs)
        {
            var profileName = Path.GetFileName(profileDir);
            if (string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            var labelPrefix = string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase)
                ? "Google Chrome (Default profile)"
                : $"Google Chrome ({profileName})";

            foreach (var target in ChromeSubFolders)
            {
                var candidate = Path.Combine(profileDir, target.SubPath);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                targets.Add(new CleanupTargetDefinition("Cache", $"{labelPrefix} {target.LabelSuffix}", candidate, target.Notes));
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetFirefoxCacheDefinitions()
    {
        var basePath = Path.Combine(RoamingAppData, "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(basePath))
        {
            return Array.Empty<CleanupTargetDefinition>();
        }

        var targets = new List<CleanupTargetDefinition>();

        foreach (var profileDir in SafeEnumerateDirectories(basePath))
        {
            var cachePath = Path.Combine(profileDir, "cache2");
            if (!Directory.Exists(cachePath))
            {
                continue;
            }

            var profileName = Path.GetFileName(profileDir) ?? "Profile";
            targets.Add(new CleanupTargetDefinition("Cache", $"Mozilla Firefox ({profileName})", cachePath, "Firefox disk cache. Close Firefox before cleaning."));
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetTeamsCacheDefinitions()
    {
        var root = Path.Combine(LocalAppData, "Microsoft", "Teams");
        if (!Directory.Exists(root))
        {
            return Array.Empty<CleanupTargetDefinition>();
        }

        var subFolders = new[]
        {
            "Cache",
            "Code Cache",
            "GPUCache",
            "databases",
            "IndexedDB",
            "Local Storage",
            "blob_storage",
            Path.Combine("Service Worker", "CacheStorage")
        };

        var targets = new List<CleanupTargetDefinition>();

        foreach (var subFolder in subFolders)
        {
            var candidate = Path.Combine(root, subFolder);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            targets.Add(new CleanupTargetDefinition("Cache", $"Microsoft Teams ({subFolder})", candidate, "Microsoft Teams application caches. Close Teams before cleaning."));
        }

        return targets;
    }

    private static IReadOnlyList<(string SubPath, string LabelSuffix, string Notes)> EdgeSubFolders { get; } = new[]
    {
        ("Cache", "Cache", "Browser cache for Microsoft Edge profiles. Close Edge before cleaning."),
        ("Code Cache", "Code Cache", "JavaScript bytecode cache for Microsoft Edge profiles."),
        ("GPUCache", "GPU Cache", "GPU shader cache for Microsoft Edge profiles."),
        (Path.Combine("Service Worker", "CacheStorage"), "Service Worker Cache", "Service Worker cache data for Microsoft Edge profiles."),
    };

    private static IReadOnlyList<(string SubPath, string LabelSuffix, string Notes)> ChromeSubFolders { get; } = new[]
    {
        ("Cache", "Cache", "Browser cache for Google Chrome profiles. Close Chrome before cleaning."),
        ("Code Cache", "Code Cache", "JavaScript bytecode cache for Google Chrome profiles."),
        ("GPUCache", "GPU Cache", "GPU shader cache for Google Chrome profiles."),
        (Path.Combine("Service Worker", "CacheStorage"), "Service Worker Cache", "Service Worker cache data for Google Chrome profiles."),
    };

    private static IReadOnlyList<(string FileName, string LabelSuffix, string Notes)> ChromiumHistoryFiles { get; } = new[]
    {
        ("History", "Browsing history", "Clears site visit history. Close the browser before cleaning."),
        ("History-journal", "History journal", "Removes the SQLite journal so history cannot be restored."),
        ("History-wal", "History WAL", "Removes the write-ahead log to wipe pending browser history."),
        ("History-shm", "History shared memory", "Removes the SQLite shared-memory file for browser history."),
        ("History Provider Cache", "History provider cache", "Clears omnibox history suggestions."),
        ("History Provider Cache-journal", "History provider cache journal", "Removes the journal for the history provider cache."),
        ("History Provider Cache-wal", "History provider cache WAL", "Removes outstanding cached history provider entries."),
        ("History Provider Cache-shm", "History provider cache shared memory", "Clears residual provider cache state."),
        ("Visited Links", "Visited links cache", "Removes colored/auto-complete visited link hints."),
        ("Visited Links-journal", "Visited links journal", "Removes the journal file for visited links cache."),
        ("Network Action Predictor", "Prediction data", "Clears predictive navigation data for the profile."),
    };

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static CleanupTargetDefinition? TryCreateFileDefinition(string classification, string category, string? path, string notes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        return new CleanupTargetDefinition(classification, category, path, notes, CleanupTargetType.File);
    }

    private static bool IsChromeProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith("Guest Profile", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Combine(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        if (segments is null || segments.Length == 0)
        {
            return root;
        }

        var parts = new List<string>(segments.Length + 1) { root };
        foreach (var segment in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                parts.Add(segment);
            }
        }

        return Path.Combine(parts.ToArray());
    }
}
