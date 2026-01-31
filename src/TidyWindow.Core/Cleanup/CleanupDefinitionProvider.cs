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
        definitions.AddRange(GetBraveCacheDefinitions());
        definitions.AddRange(GetOperaCacheDefinitions());
        definitions.AddRange(GetVivaldiCacheDefinitions());
        definitions.AddRange(GetTeamsCacheDefinitions());
        definitions.AddRange(GetNewTeamsCacheDefinitions());
        if (includeBrowserHistory)
        {
            definitions.AddRange(GetEdgeHistoryDefinitions());
            definitions.AddRange(GetChromeHistoryDefinitions());
            definitions.AddRange(GetBraveHistoryDefinitions());
            definitions.AddRange(GetOperaHistoryDefinitions());
            definitions.AddRange(GetVivaldiHistoryDefinitions());
        }
        definitions.AddRange(GetAdditionalSafeTargets());
        definitions.AddRange(GetWindowsUpgradeResidueTargets());
        definitions.AddRange(GetThumbnailAndIconCacheTargets());
        definitions.AddRange(GetRecycleBinTargets());
        definitions.AddRange(GetRecentFilesTargets());
        definitions.AddRange(GetWindowsAICopilotTargets());
        definitions.AddRange(GetCrashDumpTargets());
        definitions.AddRange(GetWindowsLogTargets());
        definitions.AddRange(GetInstallerResidueTargets());
        definitions.AddRange(GetOfficeAndProductivityTargets());
        definitions.AddRange(GetMessagingAppTargets());
        definitions.AddRange(GetGameLauncherTargets());
        definitions.AddRange(GetGpuCacheTargets());
        definitions.AddRange(GetDeveloperToolTargets());
        definitions.AddRange(GetAdditionalDevToolTargets());
        definitions.AddRange(GetAppLogTargets());
        definitions.AddRange(GetFontCacheTargets());
        definitions.AddRange(GetSpotlightAndLockScreenTargets());
        definitions.AddRange(GetSearchIndexTargets());
        definitions.AddRange(GetMediaPlayerTargets());
        definitions.AddRange(GetAdobeTargets());
        definitions.AddRange(GetDiscordAndCommunicationTargets());
        definitions.AddRange(GetCloudStorageTargets());
        definitions.AddRange(GetVirtualizationTargets());
        definitions.AddRange(GetMiscellaneousAppTargets());

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

    private static IEnumerable<CleanupTargetDefinition> GetNewTeamsCacheDefinitions()
    {
        // New Teams (Teams 2.0) stores data in different locations
        var targets = new List<CleanupTargetDefinition>();

        // New Teams uses Packages folder
        var packagesRoot = Path.Combine(LocalAppData, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            foreach (var pkg in SafeEnumerateDirectories(packagesRoot))
            {
                var name = Path.GetFileName(pkg);
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("MSTeams_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var localCache = Path.Combine(pkg, "LocalCache");
                if (Directory.Exists(localCache))
                {
                    AddDirectoryTarget(targets, "Cache", $"New Teams ({name}) LocalCache", localCache, "New Microsoft Teams (2.0) local cache files.");
                }

                var tempPath = Path.Combine(pkg, "AC", "Temp");
                if (Directory.Exists(tempPath))
                {
                    AddDirectoryTarget(targets, "Cache", $"New Teams ({name}) Temp", tempPath, "New Microsoft Teams (2.0) temporary files.");
                }
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetBraveCacheDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data");
        return GetChromiumCacheDefinitions(basePath, "Brave Browser", BraveSubFolders);
    }

    private static IEnumerable<CleanupTargetDefinition> GetOperaCacheDefinitions()
    {
        var basePath = Path.Combine(RoamingAppData, "Opera Software", "Opera Stable");
        return GetChromiumCacheDefinitions(basePath, "Opera", OperaSubFolders, isRoamingProfile: true);
    }

    private static IEnumerable<CleanupTargetDefinition> GetVivaldiCacheDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "Vivaldi", "User Data");
        return GetChromiumCacheDefinitions(basePath, "Vivaldi", VivaldiSubFolders);
    }

    private static IEnumerable<CleanupTargetDefinition> GetChromiumCacheDefinitions(string basePath, string browserLabel, IReadOnlyList<(string SubPath, string LabelSuffix, string Notes)> subFolders, bool isRoamingProfile = false)
    {
        if (!Directory.Exists(basePath))
        {
            return Array.Empty<CleanupTargetDefinition>();
        }

        var targets = new List<CleanupTargetDefinition>();

        // For Opera, the basePath itself is the profile
        if (isRoamingProfile)
        {
            foreach (var target in subFolders)
            {
                var candidate = Path.Combine(basePath, target.SubPath);
                if (Directory.Exists(candidate))
                {
                    targets.Add(new CleanupTargetDefinition("Cache", $"{browserLabel} {target.LabelSuffix}", candidate, target.Notes.Replace("{Browser}", browserLabel)));
                }
            }
            return targets;
        }

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
                ? $"{browserLabel} (Default profile)"
                : $"{browserLabel} ({profileName})";

            foreach (var target in subFolders)
            {
                var candidate = Path.Combine(profileDir, target.SubPath);
                if (Directory.Exists(candidate))
                {
                    targets.Add(new CleanupTargetDefinition("Cache", $"{labelPrefix} {target.LabelSuffix}", candidate, target.Notes.Replace("{Browser}", browserLabel)));
                }
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetBraveHistoryDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data");
        return GetChromiumHistoryDefinitions(basePath, "Brave Browser");
    }

    private static IEnumerable<CleanupTargetDefinition> GetOperaHistoryDefinitions()
    {
        var basePath = Path.Combine(RoamingAppData, "Opera Software", "Opera Stable");
        return GetChromiumHistoryDefinitions(basePath, "Opera");
    }

    private static IEnumerable<CleanupTargetDefinition> GetVivaldiHistoryDefinitions()
    {
        var basePath = Path.Combine(LocalAppData, "Vivaldi", "User Data");
        return GetChromiumHistoryDefinitions(basePath, "Vivaldi");
    }

    private static IEnumerable<CleanupTargetDefinition> GetWindowsUpgradeResidueTargets()
    {
        var targets = new List<CleanupTargetDefinition>();
        var systemDrive = GetSystemDrive();

        AddDirectoryTarget(targets, "Orphaned", "Windows.old", Path.Combine(systemDrive, "Windows.old"), "Previous Windows installation. Can reclaim 10-30 GB after major updates. Safe to delete after upgrade verification.");
        AddDirectoryTarget(targets, "Orphaned", "Windows Update staging", Path.Combine(systemDrive, "$Windows.~WS"), "Windows Update staging folder from feature updates.");
        AddDirectoryTarget(targets, "Orphaned", "Windows Download staging", Path.Combine(systemDrive, "$Windows.~BT"), "Windows upgrade download and staging folder.");
        AddDirectoryTarget(targets, "Orphaned", "Windows Upgrade", Path.Combine(systemDrive, "$WINDOWS.~Q"), "Windows upgrade temporary files.");
        AddDirectoryTarget(targets, "Orphaned", "GetCurrent folder", Path.Combine(systemDrive, "$GetCurrent"), "Windows Update Assistant temporary folder.");
        AddDirectoryTarget(targets, "Orphaned", "SysReset Temp", Path.Combine(systemDrive, "$SysReset"), "System Reset temporary files.");
        AddDirectoryTarget(targets, "Installer", "Windows Installer temp", Combine(WindowsDirectory, "Installer", "$PatchCache$"), "Windows Installer patch cache baseline.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetThumbnailAndIconCacheTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Windows Explorer thumbnail cache
        var thumbCachePath = Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer");
        if (Directory.Exists(thumbCachePath))
        {
            AddDirectoryTarget(targets, "Cache", "Explorer thumbnail cache", thumbCachePath, "Windows Explorer thumbnail cache files (thumbcache_*.db). Regenerated automatically.");
        }

        // Icon cache
        AddFileTarget(targets, "Cache", "Icon cache", Path.Combine(LocalAppData, "IconCache.db"), "Windows icon cache database. Regenerated on restart.");

        // Newer icon cache location
        var iconCacheDir = Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer");
        if (Directory.Exists(iconCacheDir))
        {
            foreach (var file in SafeEnumerateFiles(iconCacheDir, "iconcache_*.db"))
            {
                var fileName = Path.GetFileName(file);
                AddFileTarget(targets, "Cache", $"Icon cache ({fileName})", file, "Windows icon cache database. Regenerated automatically.");
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetRecycleBinTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Get all fixed drives
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var recycleBin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            if (Directory.Exists(recycleBin))
            {
                AddDirectoryTarget(targets, "Orphaned", $"Recycle Bin ({drive.Name.TrimEnd('\\')})", recycleBin, "Deleted files in Recycle Bin waiting to be permanently removed.");
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetRecentFilesTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Recent files
        AddDirectoryTarget(targets, "History", "Recent files list", Combine(RoamingAppData, "Microsoft", "Windows", "Recent"), "List of recently opened files. Clears file access history.");

        // Jump Lists
        AddDirectoryTarget(targets, "History", "Jump Lists (Automatic)", Combine(RoamingAppData, "Microsoft", "Windows", "Recent", "AutomaticDestinations"), "Automatic jump list data for taskbar pins.");
        AddDirectoryTarget(targets, "History", "Jump Lists (Custom)", Combine(RoamingAppData, "Microsoft", "Windows", "Recent", "CustomDestinations"), "Custom jump list data for frequently used items.");

        // Network shortcuts
        AddDirectoryTarget(targets, "History", "Network shortcuts", Combine(RoamingAppData, "Microsoft", "Windows", "Network Shortcuts"), "Network location shortcuts.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetWindowsAICopilotTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Windows Recall data
        AddDirectoryTarget(targets, "Cache", "Windows Recall snapshots", Combine(LocalAppData, "CoreAIPlatform.00", "UKP"), "Windows Recall AI snapshots and screenshot data.");
        AddDirectoryTarget(targets, "Cache", "Windows Recall database", Combine(LocalAppData, "CoreAIPlatform.00"), "Windows Recall AI database and metadata.");

        // Copilot caches
        AddDirectoryTarget(targets, "Cache", "Copilot cache", Combine(LocalAppData, "Packages", "Microsoft.Copilot_8wekyb3d8bbwe", "LocalCache"), "Windows Copilot application cache.");
        AddDirectoryTarget(targets, "Cache", "Copilot temp", Combine(LocalAppData, "Packages", "Microsoft.Copilot_8wekyb3d8bbwe", "AC", "Temp"), "Windows Copilot temporary files.");

        // AI Host data
        AddDirectoryTarget(targets, "Cache", "AI Host cache", Combine(LocalAppData, "Microsoft", "Windows", "AIHost"), "Windows AI Host runtime cache.");

        // Semantic Index
        AddDirectoryTarget(targets, "Cache", "Semantic Index", Combine(LocalAppData, "Packages", "MicrosoftWindows.Client.AIX_cw5n1h2txyewy", "LocalCache"), "Windows Semantic Index AI cache.");

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

    private static IEnumerable<CleanupTargetDefinition> GetMessagingAppTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Slack
        AddDirectoryTarget(targets, "Cache", "Slack Cache", Combine(RoamingAppData, "Slack", "Cache"), "Slack desktop app cache. Close Slack before cleaning.");
        AddDirectoryTarget(targets, "Cache", "Slack Code Cache", Combine(RoamingAppData, "Slack", "Code Cache"), "Slack JavaScript bytecode cache.");
        AddDirectoryTarget(targets, "Cache", "Slack GPU Cache", Combine(RoamingAppData, "Slack", "GPUCache"), "Slack GPU shader cache.");
        AddDirectoryTarget(targets, "Cache", "Slack Service Worker", Combine(RoamingAppData, "Slack", "Service Worker", "CacheStorage"), "Slack Service Worker cache.");
        AddDirectoryTarget(targets, "Logs", "Slack logs", Combine(RoamingAppData, "Slack", "logs"), "Slack diagnostic logs.");

        // Zoom
        AddDirectoryTarget(targets, "Cache", "Zoom data", Combine(RoamingAppData, "Zoom", "data"), "Zoom cached meeting data.");
        AddDirectoryTarget(targets, "Logs", "Zoom logs", Combine(RoamingAppData, "Zoom", "logs"), "Zoom meeting and diagnostic logs.");

        // WhatsApp Desktop
        AddDirectoryTarget(targets, "Cache", "WhatsApp Cache", Combine(RoamingAppData, "WhatsApp", "Cache"), "WhatsApp desktop cache.");
        AddDirectoryTarget(targets, "Cache", "WhatsApp IndexedDB", Combine(RoamingAppData, "WhatsApp", "IndexedDB"), "WhatsApp local database cache.");

        // Telegram
        AddDirectoryTarget(targets, "Cache", "Telegram cache", Combine(RoamingAppData, "Telegram Desktop", "tdata", "user_data"), "Telegram Desktop cached media.");

        // Signal
        AddDirectoryTarget(targets, "Cache", "Signal attachments cache", Combine(RoamingAppData, "Signal", "attachments.noindex"), "Signal cached media attachments.");

        // Skype
        AddDirectoryTarget(targets, "Cache", "Skype Cache", Combine(RoamingAppData, "Microsoft", "Skype for Desktop", "Cache"), "Skype desktop cache files.");
        AddDirectoryTarget(targets, "Cache", "Skype media cache", Combine(RoamingAppData, "Microsoft", "Skype for Desktop", "Media Cache"), "Skype media cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetAdditionalDevToolTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // npm cache
        AddDirectoryTarget(targets, "Cache", "npm cache", Combine(RoamingAppData, "npm-cache"), "Node.js npm package cache. Safe to clear; packages will re-download.");

        // Yarn cache
        AddDirectoryTarget(targets, "Cache", "Yarn cache", Combine(LocalAppData, "Yarn", "Cache"), "Yarn package manager cache.");

        // pnpm cache
        AddDirectoryTarget(targets, "Cache", "pnpm cache", Combine(LocalAppData, "pnpm-cache"), "pnpm package manager store cache.");

        // pip cache
        AddDirectoryTarget(targets, "Cache", "pip cache", Combine(LocalAppData, "pip", "Cache"), "Python pip package cache.");

        // Gradle cache
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddDirectoryTarget(targets, "Cache", "Gradle caches", Path.Combine(userProfile, ".gradle", "caches"), "Gradle build system cache.");
            AddDirectoryTarget(targets, "Cache", "Gradle wrapper", Path.Combine(userProfile, ".gradle", "wrapper", "dists"), "Gradle wrapper distribution cache.");

            // Maven cache
            AddDirectoryTarget(targets, "Cache", "Maven repository", Path.Combine(userProfile, ".m2", "repository"), "Maven local repository cache. Warning: May need re-download.");

            // Cargo (Rust)
            AddDirectoryTarget(targets, "Cache", "Cargo registry cache", Path.Combine(userProfile, ".cargo", "registry", "cache"), "Rust Cargo registry cache.");

            // Go modules
            AddDirectoryTarget(targets, "Cache", "Go module cache", Path.Combine(userProfile, "go", "pkg", "mod", "cache"), "Go module download cache.");

            // Composer (PHP)
            AddDirectoryTarget(targets, "Cache", "Composer cache", Path.Combine(userProfile, ".composer", "cache"), "PHP Composer package cache.");

            // Nuget fallback
            AddDirectoryTarget(targets, "Cache", "NuGet fallback", Path.Combine(userProfile, ".nuget", "packages"), "NuGet global packages folder. Warning: Required for builds.");
        }

        // Docker Desktop
        AddDirectoryTarget(targets, "Cache", "Docker Desktop data", Combine(LocalAppData, "Docker", "wsl", "data"), "Docker Desktop WSL data. Warning: Contains container data.");
        AddDirectoryTarget(targets, "Logs", "Docker logs", Combine(LocalAppData, "Docker", "log"), "Docker Desktop logs.");

        // Android Studio / SDK
        AddDirectoryTarget(targets, "Cache", "Android Gradle cache", Combine(LocalAppData, "Android", "Sdk", ".temp"), "Android SDK temporary files.");

        // Electron apps common
        AddDirectoryTarget(targets, "Cache", "Electron GPU cache", Combine(RoamingAppData, "Electron", "GPUCache"), "Generic Electron GPU cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetFontCacheTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "Windows Font Cache", Combine(WindowsDirectory, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache"), "Windows font cache files.");
        AddFileTarget(targets, "Cache", "Font cache data", Combine(LocalAppData, "Microsoft", "Windows", "Fonts", "*.tmp"), "User font cache temporary files.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetSpotlightAndLockScreenTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Windows Spotlight images
        AddDirectoryTarget(targets, "Cache", "Windows Spotlight assets", Combine(LocalAppData, "Packages", "Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy", "LocalState", "Assets"), "Windows Spotlight lock screen images. New images will download.");

        // Widgets cache
        AddDirectoryTarget(targets, "Cache", "Windows Widgets cache", Combine(LocalAppData, "Packages", "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy", "LocalCache"), "Windows Widgets cached data.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetSearchIndexTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "Windows Search index", Combine(ProgramData, "Microsoft", "Search", "Data", "Applications", "Windows"), "Windows Search index database. Will rebuild automatically.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetMediaPlayerTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // VLC
        AddDirectoryTarget(targets, "Cache", "VLC art cache", Combine(RoamingAppData, "vlc", "art"), "VLC media player album art cache.");

        // Windows Media Player
        AddDirectoryTarget(targets, "Cache", "Windows Media Player cache", Combine(LocalAppData, "Microsoft", "Media Player"), "Windows Media Player database and cache.");

        // Spotify
        AddDirectoryTarget(targets, "Cache", "Spotify cache", Combine(LocalAppData, "Spotify", "Storage"), "Spotify music streaming cache.");
        AddDirectoryTarget(targets, "Cache", "Spotify data", Combine(LocalAppData, "Spotify", "Data"), "Spotify cached data.");

        // iTunes
        AddDirectoryTarget(targets, "Cache", "iTunes cache", Combine(LocalAppData, "Apple Computer", "iTunes"), "iTunes media cache.");

        // Plex
        AddDirectoryTarget(targets, "Cache", "Plex cache", Combine(LocalAppData, "Plex Media Server", "Cache"), "Plex Media Server cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetAdobeTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Adobe Common
        AddDirectoryTarget(targets, "Cache", "Adobe cache", Combine(LocalAppData, "Adobe"), "Adobe application cache files.");
        AddDirectoryTarget(targets, "Cache", "Adobe roaming", Combine(RoamingAppData, "Adobe"), "Adobe roaming application data.");

        // Creative Cloud
        AddDirectoryTarget(targets, "Cache", "Creative Cloud logs", Combine(LocalAppData, "Adobe", "Creative Cloud Libraries", "LIBS", "librarylookupfile"), "Adobe Creative Cloud lookup cache.");

        // Acrobat Reader
        AddDirectoryTarget(targets, "Cache", "Acrobat cache", Combine(LocalAppData, "Adobe", "Acrobat"), "Adobe Acrobat cache and temporary files.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetDiscordAndCommunicationTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Discord
        AddDirectoryTarget(targets, "Cache", "Discord Cache", Combine(RoamingAppData, "discord", "Cache"), "Discord cache files. Close Discord before cleaning.");
        AddDirectoryTarget(targets, "Cache", "Discord Code Cache", Combine(RoamingAppData, "discord", "Code Cache"), "Discord JavaScript cache.");
        AddDirectoryTarget(targets, "Cache", "Discord GPU Cache", Combine(RoamingAppData, "discord", "GPUCache"), "Discord GPU shader cache.");
        AddDirectoryTarget(targets, "Logs", "Discord logs", Combine(RoamingAppData, "discord", "logs"), "Discord diagnostic logs.");

        // Element (Matrix client)
        AddDirectoryTarget(targets, "Cache", "Element cache", Combine(RoamingAppData, "Element", "Cache"), "Element messenger cache.");

        // Guilded
        AddDirectoryTarget(targets, "Cache", "Guilded cache", Combine(RoamingAppData, "Guilded", "Cache"), "Guilded gaming chat cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetCloudStorageTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // OneDrive
        AddDirectoryTarget(targets, "Logs", "OneDrive logs", Combine(LocalAppData, "Microsoft", "OneDrive", "logs"), "OneDrive sync logs.");
        AddDirectoryTarget(targets, "Cache", "OneDrive setup logs", Combine(LocalAppData, "Microsoft", "OneDrive", "setup", "logs"), "OneDrive setup and update logs.");

        // Google Drive
        AddDirectoryTarget(targets, "Cache", "Google Drive cache", Combine(LocalAppData, "Google", "DriveFS"), "Google Drive for Desktop cache and sync data.");
        AddDirectoryTarget(targets, "Logs", "Google Drive logs", Combine(LocalAppData, "Google", "DriveFS", "Logs"), "Google Drive sync logs.");

        // Dropbox
        AddDirectoryTarget(targets, "Cache", "Dropbox cache", Combine(LocalAppData, "Dropbox"), "Dropbox local cache.");

        // iCloud
        AddDirectoryTarget(targets, "Cache", "iCloud cache", Combine(LocalAppData, "Apple Inc", "iCloud"), "iCloud for Windows cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetVirtualizationTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // VMware
        AddDirectoryTarget(targets, "Logs", "VMware logs", Combine(RoamingAppData, "VMware"), "VMware Workstation logs.");

        // VirtualBox
        AddDirectoryTarget(targets, "Logs", "VirtualBox logs", Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "VirtualBox VMs"), "VirtualBox VM logs. Warning: Contains VM data.");

        // WSL
        AddDirectoryTarget(targets, "Cache", "WSL temp", Combine(LocalAppData, "Temp", "wsl"), "Windows Subsystem for Linux temporary files.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetMiscellaneousAppTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // PowerToys
        AddDirectoryTarget(targets, "Logs", "PowerToys logs", Combine(LocalAppData, "Microsoft", "PowerToys", "Logs"), "Microsoft PowerToys logs.");

        // Windows Terminal
        AddDirectoryTarget(targets, "Cache", "Windows Terminal state", Combine(LocalAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState"), "Windows Terminal saved state and settings backup.");

        // Clipboard history
        AddDirectoryTarget(targets, "History", "Clipboard history", Combine(LocalAppData, "Microsoft", "Windows", "Clipboard"), "Windows clipboard history data.");

        // Windows Quick Assist
        AddDirectoryTarget(targets, "Logs", "Quick Assist logs", Combine(LocalAppData, "Packages", "MicrosoftCorporationII.QuickAssist_8wekyb3d8bbwe", "LocalState"), "Windows Quick Assist session data.");

        // Paint 3D
        AddDirectoryTarget(targets, "Cache", "Paint 3D cache", Combine(LocalAppData, "Packages", "Microsoft.MSPaint_8wekyb3d8bbwe", "LocalCache"), "Paint 3D application cache.");

        // Snipping Tool
        AddDirectoryTarget(targets, "Cache", "Snipping Tool cache", Combine(LocalAppData, "Packages", "Microsoft.ScreenSketch_8wekyb3d8bbwe", "LocalCache"), "Snipping Tool cache and temporary screenshots.");

        // Photos app
        AddDirectoryTarget(targets, "Cache", "Photos app cache", Combine(LocalAppData, "Packages", "Microsoft.Windows.Photos_8wekyb3d8bbwe", "LocalCache"), "Windows Photos app cache.");

        // Calculator
        AddDirectoryTarget(targets, "Cache", "Calculator app cache", Combine(LocalAppData, "Packages", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "LocalCache"), "Windows Calculator app cache.");

        // Maps
        AddDirectoryTarget(targets, "Cache", "Windows Maps cache", Combine(LocalAppData, "Packages", "Microsoft.WindowsMaps_8wekyb3d8bbwe", "LocalCache"), "Windows Maps offline cache.");

        // Weather
        AddDirectoryTarget(targets, "Cache", "Weather app cache", Combine(LocalAppData, "Packages", "Microsoft.BingWeather_8wekyb3d8bbwe", "LocalCache"), "Weather app cached data.");

        // News
        AddDirectoryTarget(targets, "Cache", "News app cache", Combine(LocalAppData, "Packages", "Microsoft.BingNews_8wekyb3d8bbwe", "LocalCache"), "News app cached articles and images.");

        // Get Help
        AddDirectoryTarget(targets, "Cache", "Get Help cache", Combine(LocalAppData, "Packages", "Microsoft.GetHelp_8wekyb3d8bbwe", "LocalCache"), "Get Help app cache.");

        // Cortana
        AddDirectoryTarget(targets, "Cache", "Cortana cache", Combine(LocalAppData, "Packages", "Microsoft.549981C3F5F10_8wekyb3d8bbwe", "LocalCache"), "Cortana app cache.");

        // Razer Synapse
        AddDirectoryTarget(targets, "Logs", "Razer Synapse logs", Combine(ProgramData, "Razer", "Synapse", "Logs"), "Razer Synapse peripheral software logs.");
        AddDirectoryTarget(targets, "Cache", "Razer cache", Combine(LocalAppData, "Razer"), "Razer software cache.");

        // Logitech
        AddDirectoryTarget(targets, "Cache", "Logitech cache", Combine(LocalAppData, "Logitech"), "Logitech software cache.");

        // Corsair iCUE
        AddDirectoryTarget(targets, "Logs", "Corsair iCUE logs", Combine(RoamingAppData, "Corsair", "CUE", "logs"), "Corsair iCUE software logs.");

        // SteelSeries GG
        AddDirectoryTarget(targets, "Logs", "SteelSeries logs", Combine(ProgramData, "SteelSeries", "GG", "logs"), "SteelSeries GG software logs.");

        // 7-Zip
        AddDirectoryTarget(targets, "History", "7-Zip history", Combine(RoamingAppData, "7-Zip"), "7-Zip extraction history.");

        // WinRAR
        AddDirectoryTarget(targets, "History", "WinRAR history", Combine(RoamingAppData, "WinRAR"), "WinRAR archive history.");

        return targets;
    }

    private static string GetSystemDrive()
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

        return systemDrive;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string searchPattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, searchPattern);
        }
        catch
        {
            return Array.Empty<string>();
        }
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

    private static IReadOnlyList<(string SubPath, string LabelSuffix, string Notes)> BraveSubFolders { get; } = new[]
    {
        ("Cache", "Cache", "Browser cache for {Browser} profiles. Close browser before cleaning."),
        ("Code Cache", "Code Cache", "JavaScript bytecode cache for {Browser} profiles."),
        ("GPUCache", "GPU Cache", "GPU shader cache for {Browser} profiles."),
        (Path.Combine("Service Worker", "CacheStorage"), "Service Worker Cache", "Service Worker cache data for {Browser} profiles."),
    };

    private static IReadOnlyList<(string SubPath, string LabelSuffix, string Notes)> OperaSubFolders { get; } = new[]
    {
        ("Cache", "Cache", "Browser cache for {Browser}. Close browser before cleaning."),
        ("Code Cache", "Code Cache", "JavaScript bytecode cache for {Browser}."),
        ("GPUCache", "GPU Cache", "GPU shader cache for {Browser}."),
        (Path.Combine("Service Worker", "CacheStorage"), "Service Worker Cache", "Service Worker cache for {Browser}."),
    };

    private static IReadOnlyList<(string SubPath, string LabelSuffix, string Notes)> VivaldiSubFolders { get; } = new[]
    {
        ("Cache", "Cache", "Browser cache for {Browser} profiles. Close browser before cleaning."),
        ("Code Cache", "Code Cache", "JavaScript bytecode cache for {Browser} profiles."),
        ("GPUCache", "GPU Cache", "GPU shader cache for {Browser} profiles."),
        (Path.Combine("Service Worker", "CacheStorage"), "Service Worker Cache", "Service Worker cache for {Browser} profiles."),
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
