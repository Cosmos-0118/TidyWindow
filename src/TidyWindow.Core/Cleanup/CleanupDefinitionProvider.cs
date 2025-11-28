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
    private static readonly string DefaultUserProfile = GetDefaultUserProfilePath();

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
        definitions.AddRange(GetCrashDumpTargets());
        definitions.AddRange(GetWindowsLogTargets());
        definitions.AddRange(GetInstallerResidueTargets());
        definitions.AddRange(GetOfficeAndProductivityTargets());
        definitions.AddRange(GetGameLauncherTargets());
        definitions.AddRange(GetGpuCacheTargets());
        definitions.AddRange(GetDeveloperToolTargets());
        definitions.AddRange(GetAppLogTargets());

        definitions.AddRange(new[]
        {
            new CleanupTargetDefinition("Logs", "Windows Update Logs", Combine(WindowsDirectory, "Logs", "WindowsUpdate"), "Windows Update diagnostic logs."),
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

            var thumbnailsPath = Path.Combine(profileDir, "thumbnails");
            if (Directory.Exists(thumbnailsPath))
            {
                targets.Add(new CleanupTargetDefinition("Cache", $"Mozilla Firefox ({profileName}) thumbnails", thumbnailsPath, "Firefox thumbnail cache used for new tab previews."));
            }
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

    private static IEnumerable<CleanupTargetDefinition> GetCrashDumpTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Logs", "Windows Error Reporting Queue", Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"), "Queued Windows Error Reporting crash dumps and diagnostics.");
        AddDirectoryTarget(targets, "Logs", "Windows Error Reporting Archive", Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), "Stored Windows Error Reporting results that are safe to purge.");
        AddDirectoryTarget(targets, "Logs", "Windows Error Reporting Temp", Combine(ProgramData, "Microsoft", "Windows", "WER", "Temp"), "Temporary files generated by Windows Error Reporting.");
        AddDirectoryTarget(targets, "Orphaned", "ProgramData Crash Dumps", Combine(ProgramData, "CrashDumps"), "Crash dumps captured for system services running under service accounts.");
        AddDirectoryTarget(targets, "Orphaned", "Default profile crash dumps", Combine(DefaultUserProfile, "AppData", "Local", "CrashDumps"), "Crash dumps created before any user signs in.");
        AddDirectoryTarget(targets, "Orphaned", "Live Kernel Reports", Combine(WindowsDirectory, "LiveKernelReports"), "Live kernel reports and watchdog dumps.");
        AddFileTarget(targets, "Orphaned", "Memory dump", Combine(WindowsDirectory, "memory.dmp"), "Full system memory crash dump.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetWindowsLogTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Logs", "CBS logs", Combine(WindowsDirectory, "Logs", "CBS"), "Component-Based Servicing logs used during servicing operations.");
        AddDirectoryTarget(targets, "Logs", "DISM logs", Combine(WindowsDirectory, "Logs", "DISM"), "Deployment Image Servicing and Management logs.");
        AddDirectoryTarget(targets, "Logs", "MoSetup logs", Combine(WindowsDirectory, "Logs", "MoSetup"), "Modern setup logs generated by feature updates.");
        AddDirectoryTarget(targets, "Logs", "Panther setup logs", Combine(WindowsDirectory, "Panther"), "Windows setup migration logs.");
        AddDirectoryTarget(targets, "Logs", "USO Update Store", Combine(ProgramData, "USOPrivate", "UpdateStore"), "Windows Update Orchestrator metadata cache.");
        AddFileTarget(targets, "Logs", "Setup API app log", Combine(WindowsDirectory, "inf", "setupapi.app.log"), "Verbose setup API log.");
        AddFileTarget(targets, "Logs", "Setup API device log", Combine(WindowsDirectory, "inf", "setupapi.dev.log"), "Driver installation log.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetInstallerResidueTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Installer", "Package Cache", Combine(ProgramData, "Package Cache"), "Cached installer payloads left behind by setup engines.");
        AddDirectoryTarget(targets, "Installer", "Patch Cache", Combine(ProgramData, "Microsoft", "Windows", "Installer", "$PatchCache$"), "Windows Installer baseline cache used for patching.");
        AddDirectoryTarget(targets, "Installer", "User Package Cache", Combine(LocalAppData, "Package Cache"), "Per-user package caches and installer logs.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetOfficeAndProductivityTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "Office File Cache", Combine(LocalAppData, "Microsoft", "Office", "16.0", "OfficeFileCache"), "Microsoft 365 document cache.");
        AddDirectoryTarget(targets, "Cache", "Office WEF cache", Combine(LocalAppData, "Microsoft", "Office", "16.0", "Wef"), "Web Extension Framework cache for Office add-ins.");
        AddDirectoryTarget(targets, "Logs", "OneDrive logs", Combine(LocalAppData, "Microsoft", "OneDrive", "logs"), "OneDrive diagnostic logs.");

        foreach (var backup in GetOneNoteBackupFolders())
        {
            AddDirectoryTarget(targets, "Cache", $"OneNote {backup.Label} backups", backup.Path, "OneNote local backup folders.");
        }

        return targets;
    }

    private static IEnumerable<(string Label, string Path)> GetOneNoteBackupFolders()
    {
        var results = new List<(string, string)>();
        var root = Path.Combine(LocalAppData, "Microsoft", "OneNote");
        if (!Directory.Exists(root))
        {
            return results;
        }

        foreach (var versionDir in SafeEnumerateDirectories(root))
        {
            var backupPath = Path.Combine(versionDir, "Backup");
            if (Directory.Exists(backupPath))
            {
                var label = Path.GetFileName(versionDir) ?? "Profile";
                results.Add((label, backupPath));
            }
        }

        return results;
    }

    private static IEnumerable<CleanupTargetDefinition> GetGameLauncherTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "Steam HTML cache", Combine(LocalAppData, "Steam", "htmlcache"), "Steam browser HTML cache.");
        AddDirectoryTarget(targets, "Cache", "Steam shader cache", Combine(LocalAppData, "Steam", "shadercache"), "Steam shader cache compilation output.");
        AddDirectoryTarget(targets, "Cache", "Epic Games logs", Combine(LocalAppData, "EpicGamesLauncher", "Saved", "Logs"), "Epic Games Launcher logs.");
        AddDirectoryTarget(targets, "Cache", "Epic Games webcache", Combine(LocalAppData, "EpicGamesLauncher", "Saved", "webcache"), "Epic Games Launcher web cache.");

        foreach (var packageTemp in EnumeratePackageTempFolders("Microsoft.Xbox"))
        {
            AddDirectoryTarget(targets, "Cache", $"{packageTemp.Label} temp", packageTemp.Path, "Xbox app temporary files.");
        }

        foreach (var packageTemp in EnumeratePackageTempFolders("Microsoft.GamingApp"))
        {
            AddDirectoryTarget(targets, "Cache", $"{packageTemp.Label} temp", packageTemp.Path, "Gaming Services temporary files.");
        }

        return targets;
    }

    private static IEnumerable<(string Label, string Path)> EnumeratePackageTempFolders(string prefix)
    {
        var results = new List<(string, string)>();
        var root = Path.Combine(LocalAppData, "Packages");
        if (!Directory.Exists(root))
        {
            return results;
        }

        foreach (var package in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(package);
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tempPath = Path.Combine(package, "AC", "Temp");
            if (Directory.Exists(tempPath))
            {
                results.Add(($"{name}", tempPath));
            }
        }

        return results;
    }

    private static IEnumerable<CleanupTargetDefinition> GetGpuCacheTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "NVIDIA shader cache", Combine(ProgramData, "NVIDIA Corporation", "NV_Cache"), "Global NVIDIA shader cache.");
        AddDirectoryTarget(targets, "Cache", "NVIDIA DX cache", Combine(LocalAppData, "NVIDIA", "DXCache"), "DirectX shader cache used by NVIDIA drivers.");
        AddDirectoryTarget(targets, "Cache", "NVIDIA GL cache", Combine(LocalAppData, "NVIDIA", "GLCache"), "OpenGL shader cache used by NVIDIA drivers.");
        AddDirectoryTarget(targets, "Cache", "AMD DX cache", Combine(LocalAppData, "AMD", "DxCache"), "DirectX shader cache used by AMD drivers.");
        AddDirectoryTarget(targets, "Cache", "AMD GL cache", Combine(LocalAppData, "AMD", "GLCache"), "OpenGL shader cache used by AMD drivers.");
        AddDirectoryTarget(targets, "Cache", "AMD binary cache", Combine(ProgramData, "AMD"), "AMD generated shader and installer cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetDeveloperToolTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "VS Code cache", Combine(RoamingAppData, "Code", "Cache"), "Visual Studio Code disk cache.");
        AddDirectoryTarget(targets, "Cache", "VS Code cached data", Combine(RoamingAppData, "Code", "CachedData"), "Visual Studio Code cached metadata.");
        AddDirectoryTarget(targets, "Cache", "VS Code GPU cache", Combine(RoamingAppData, "Code", "GPUCache"), "Visual Studio Code GPU cache.");

        foreach (var target in GetVisualStudioCacheFolders())
        {
            AddDirectoryTarget(targets, "Cache", target.Label, target.Path, target.Notes);
        }

        foreach (var target in GetJetBrainsCacheFolders())
        {
            AddDirectoryTarget(targets, "Cache", target.Label, target.Path, target.Notes);
        }

        return targets;
    }

    private static IEnumerable<(string Label, string Path, string Notes)> GetVisualStudioCacheFolders()
    {
        var results = new List<(string, string, string)>();
        var root = Path.Combine(LocalAppData, "Microsoft", "VisualStudio");
        if (!Directory.Exists(root))
        {
            return results;
        }

        foreach (var instance in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(instance) ?? "Visual Studio";
            var componentCache = Path.Combine(instance, "ComponentModelCache");
            if (Directory.Exists(componentCache))
            {
                results.Add(($"Visual Studio {name} ComponentModelCache", componentCache, "Component catalog cache regenerated on next launch."));
            }

            var cache = Path.Combine(instance, "Cache");
            if (Directory.Exists(cache))
            {
                results.Add(($"Visual Studio {name} Cache", cache, "General Visual Studio cache data."));
            }
        }

        return results;
    }

    private static IEnumerable<(string Label, string Path, string Notes)> GetJetBrainsCacheFolders()
    {
        var results = new List<(string, string, string)>();
        var root = Path.Combine(LocalAppData, "JetBrains");
        if (!Directory.Exists(root))
        {
            return results;
        }

        foreach (var productDir in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(productDir) ?? "JetBrains";
            var cachePath = Path.Combine(productDir, "caches");
            if (Directory.Exists(cachePath))
            {
                results.Add(($"{name} caches", cachePath, "JetBrains IDE caches."));
            }

            var logPath = Path.Combine(productDir, "log");
            if (Directory.Exists(logPath))
            {
                results.Add(($"{name} logs", logPath, "JetBrains IDE logs."));
            }
        }

        return results;
    }

    private static IEnumerable<CleanupTargetDefinition> GetAppLogTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Logs", "CrashReporter logs", Combine(LocalAppData, "CrashReporter"), "Generic app crash reporter logs.");
        AddDirectoryTarget(targets, "Logs", "Package Cache logs", Combine(LocalAppData, "Package Cache"), "Installer logs emitted by app installers.");

        foreach (var usageLog in GetClrUsageLogFolders())
        {
            AddDirectoryTarget(targets, "Logs", usageLog.Label, usageLog.Path, "CLR usage logs created by .NET runtime.");
        }

        return targets;
    }

    private static IEnumerable<(string Label, string Path)> GetClrUsageLogFolders()
    {
        var results = new List<(string, string)>();
        var root = Path.Combine(LocalAppData, "Microsoft");
        if (!Directory.Exists(root))
        {
            return results;
        }

        foreach (var candidate in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(candidate);
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("CLR_v", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var usageLogs = Path.Combine(candidate, "UsageLogs");
            if (Directory.Exists(usageLogs))
            {
                results.Add(($"{name} UsageLogs", usageLogs));
            }
        }

        return results;
    }

    private static void AddDirectoryTarget(ICollection<CleanupTargetDefinition> list, string classification, string category, string? path, string notes)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            list.Add(new CleanupTargetDefinition(classification, category, path, notes));
        }
    }

    private static void AddFileTarget(ICollection<CleanupTargetDefinition> list, string classification, string category, string? path, string notes)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            list.Add(new CleanupTargetDefinition(classification, category, path, notes, CleanupTargetType.File));
        }
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

    private static string GetDefaultUserProfilePath()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(systemDrive))
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            systemDrive = string.IsNullOrWhiteSpace(root) ? "C:\\" : root;
        }

        if (!systemDrive.EndsWith(Path.DirectorySeparatorChar))
        {
            systemDrive += Path.DirectorySeparatorChar;
        }

        return Path.Combine(systemDrive, "Users", "Default");
    }
}
