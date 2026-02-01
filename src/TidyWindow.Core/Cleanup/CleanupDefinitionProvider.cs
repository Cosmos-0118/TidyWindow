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
        definitions.AddRange(GetWindowsDefenderTargets());
        definitions.AddRange(GetPrinterAndScannerTargets());
        definitions.AddRange(GetDotNetAndRuntimeTargets());
        definitions.AddRange(GetWindowsEventLogTargets());
        definitions.AddRange(GetSystemRestoreMetadataTargets());
        definitions.AddRange(GetRemoteDesktopTargets());
        definitions.AddRange(GetDeliveryOptimizationTargets());
        definitions.AddRange(GetWindowsUpdateResidualTargets());
        definitions.AddRange(GetOtherBrowserTargets());
        definitions.AddRange(GetArchiveToolTargets());
        definitions.AddRange(GetDatabaseToolTargets());
        definitions.AddRange(GetMusicProductionTargets());
        definitions.AddRange(GetVideoEditingTargets());
        definitions.AddRange(GetGraphicsDesignTargets());
        definitions.AddRange(Get3DModelingTargets());
        definitions.AddRange(GetEmailClientTargets());
        definitions.AddRange(GetPasswordManagerTargets());
        definitions.AddRange(GetVpnClientTargets());
        definitions.AddRange(GetAntivirusTargets());
        definitions.AddRange(GetSystemToolTargets());
        definitions.AddRange(GetWindowsServiceCacheTargets());
        definitions.AddRange(GetMsixAndAppxTargets());
        definitions.AddRange(GetNetworkCacheTargets());
        definitions.AddRange(GetOtherTempLocations());
        definitions.AddRange(GetPackageManagerCacheTargets());
        definitions.AddRange(GetAdditionalGameLauncherTargets());
        definitions.AddRange(GetRemoteAccessToolTargets());
        definitions.AddRange(GetStreamingAppTargets());
        definitions.AddRange(GetNoteTakingAppTargets());
        definitions.AddRange(GetScreenshotToolTargets());
        definitions.AddRange(GetWebView2Targets());
        definitions.AddRange(GetPowerShellTargets());
        definitions.AddRange(GetWindowsSubsystemTargets());

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

    private static IEnumerable<CleanupTargetDefinition> GetWindowsDefenderTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "Windows Defender scans", Combine(ProgramData, "Microsoft", "Windows Defender", "Scans"), "Windows Defender scan history and results.");
        AddDirectoryTarget(targets, "Cache", "Windows Defender quarantine", Combine(ProgramData, "Microsoft", "Windows Defender", "Quarantine"), "Quarantined files. Warning: Contains potentially malicious files.");
        AddDirectoryTarget(targets, "Logs", "Windows Defender logs", Combine(ProgramData, "Microsoft", "Windows Defender", "Support"), "Windows Defender diagnostic logs.");
        AddDirectoryTarget(targets, "Cache", "MpCmdRun logs", Combine(WindowsDirectory, "Temp", "MpCmdRun.log"), "Microsoft Defender command-line tool logs.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetPrinterAndScannerTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "Print spooler", Combine(WindowsDirectory, "System32", "spool", "PRINTERS"), "Print spooler queue files. Clear if print jobs are stuck.");
        AddDirectoryTarget(targets, "Logs", "Print spooler logs", Combine(WindowsDirectory, "System32", "spool", "drivers", "color"), "Printer color profiles (usually safe).");
        AddDirectoryTarget(targets, "Cache", "Scan cache", Combine(LocalAppData, "Microsoft", "Windows", "WinX"), "Windows scanning cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetDotNetAndRuntimeTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            // .NET CLI
            AddDirectoryTarget(targets, "Cache", ".NET CLI temp", Path.Combine(userProfile, ".dotnet", "temp"), ".NET CLI temporary files.");
            AddDirectoryTarget(targets, "Cache", ".NET CLI tools cache", Path.Combine(userProfile, ".dotnet", "tools", ".store"), ".NET tool store (can be large).");

            // Node.js
            AddDirectoryTarget(targets, "Cache", "Node.js temp", Path.Combine(userProfile, ".node_repl_history"), "Node.js REPL history.");

            // Python
            AddDirectoryTarget(targets, "Cache", "Python cache", Path.Combine(userProfile, ".python_history"), "Python REPL history.");
            AddDirectoryTarget(targets, "Cache", "PyPI cache", Path.Combine(userProfile, "pip"), "Legacy pip cache location.");

            // Ruby
            AddDirectoryTarget(targets, "Cache", "Ruby gems cache", Path.Combine(userProfile, ".gem"), "Ruby gems cache.");
            AddDirectoryTarget(targets, "Cache", "Bundler cache", Path.Combine(userProfile, ".bundle", "cache"), "Ruby Bundler cache.");
        }

        // Windows .NET temp
        AddDirectoryTarget(targets, "Cache", ".NET Framework temp", Combine(WindowsDirectory, "Microsoft.NET", "Framework", "v4.0.30319", "Temporary ASP.NET Files"), "ASP.NET compilation temp files.");
        AddDirectoryTarget(targets, "Cache", ".NET Framework64 temp", Combine(WindowsDirectory, "Microsoft.NET", "Framework64", "v4.0.30319", "Temporary ASP.NET Files"), "ASP.NET 64-bit compilation temp.");

        // Runtime caches
        AddDirectoryTarget(targets, "Cache", "Assembly cache", Combine(WindowsDirectory, "assembly", "temp"), "Global Assembly Cache temp.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetWindowsEventLogTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Event logs can be cleared but we'll target archived/backup logs
        AddDirectoryTarget(targets, "Logs", "Archived event logs", Combine(WindowsDirectory, "System32", "winevt", "Logs"), "Windows Event Logs. Warning: Contains important system history.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetSystemRestoreMetadataTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // System Volume Information is heavily protected, but we can note it
        var systemDrive = GetSystemDrive();
        AddDirectoryTarget(targets, "Orphaned", "System restore points", Path.Combine(systemDrive, "System Volume Information"), "System restore points and VSS snapshots. Use Disk Cleanup to manage.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetRemoteDesktopTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "RDP bitmap cache", Combine(LocalAppData, "Microsoft", "Terminal Server Client", "Cache"), "Remote Desktop connection bitmap cache.");
        AddFileTarget(targets, "History", "RDP connection history", Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Documents", "Default.rdp"), "Default RDP connection file.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetDeliveryOptimizationTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "Delivery Optimization files", Combine(WindowsDirectory, "SoftwareDistribution", "DeliveryOptimization"), "Windows Update delivery optimization cache.");
        AddDirectoryTarget(targets, "Cache", "DO uploads", Combine(ProgramData, "Microsoft", "Windows", "DeliveryOptimization", "Cache"), "Delivery Optimization peer-to-peer cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetWindowsUpdateResidualTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        AddDirectoryTarget(targets, "Cache", "WU Download", Combine(WindowsDirectory, "SoftwareDistribution", "Download"), "Downloaded Windows Update files.");
        AddDirectoryTarget(targets, "Cache", "WU DataStore", Combine(WindowsDirectory, "SoftwareDistribution", "DataStore"), "Windows Update database cache.");
        AddDirectoryTarget(targets, "Cache", "Catroot2", Combine(WindowsDirectory, "System32", "catroot2"), "Cryptographic catalog database. Clears signature cache.");
        AddDirectoryTarget(targets, "Logs", "CBS Persist", Combine(WindowsDirectory, "Logs", "CBS", "Persist"), "Persistent CBS log files.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetOtherBrowserTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Waterfox
        AddDirectoryTarget(targets, "Cache", "Waterfox cache", Combine(RoamingAppData, "Waterfox", "Profiles"), "Waterfox browser profiles cache.");

        // Tor Browser
        AddDirectoryTarget(targets, "Cache", "Tor Browser cache", Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Desktop", "Tor Browser", "Browser", "TorBrowser", "Data", "Browser", "profile.default", "cache2"), "Tor Browser cache.");

        // Pale Moon
        AddDirectoryTarget(targets, "Cache", "Pale Moon cache", Combine(RoamingAppData, "Moonchild Productions", "Pale Moon", "Profiles"), "Pale Moon browser profiles.");

        // Chromium
        AddDirectoryTarget(targets, "Cache", "Chromium cache", Combine(LocalAppData, "Chromium", "User Data"), "Chromium browser cache.");

        // Arc Browser
        AddDirectoryTarget(targets, "Cache", "Arc Browser cache", Combine(LocalAppData, "Arc", "User Data"), "Arc browser cache.");

        // Floorp
        AddDirectoryTarget(targets, "Cache", "Floorp cache", Combine(RoamingAppData, "Floorp", "Profiles"), "Floorp browser cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetArchiveToolTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // PeaZip
        AddDirectoryTarget(targets, "Cache", "PeaZip temp", Combine(LocalAppData, "PeaZip"), "PeaZip archive tool temp files.");

        // Bandizip
        AddDirectoryTarget(targets, "Cache", "Bandizip temp", Combine(LocalAppData, "Bandizip"), "Bandizip archive tool cache.");

        // NanaZip
        AddDirectoryTarget(targets, "Cache", "NanaZip cache", Combine(LocalAppData, "Packages", "40174MouriNaruto.NanaZip_gnj4mf6z9tkrc", "LocalCache"), "NanaZip archive cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetDatabaseToolTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // SQL Server Management Studio
        AddDirectoryTarget(targets, "Cache", "SSMS cache", Combine(LocalAppData, "Microsoft", "SQL Server Management Studio"), "SQL Server Management Studio cache.");
        AddDirectoryTarget(targets, "Logs", "SSMS logs", Combine(RoamingAppData, "Microsoft", "SQL Server Management Studio"), "SSMS roaming data and logs.");

        // Azure Data Studio
        AddDirectoryTarget(targets, "Cache", "Azure Data Studio cache", Combine(RoamingAppData, "azuredatastudio", "Cache"), "Azure Data Studio cache.");
        AddDirectoryTarget(targets, "Cache", "Azure Data Studio CachedData", Combine(RoamingAppData, "azuredatastudio", "CachedData"), "Azure Data Studio cached extensions.");

        // DBeaver
        AddDirectoryTarget(targets, "Cache", "DBeaver cache", Combine(RoamingAppData, "DBeaverData", "workspace6", ".metadata"), "DBeaver workspace metadata.");

        // MongoDB Compass
        AddDirectoryTarget(targets, "Cache", "MongoDB Compass cache", Combine(RoamingAppData, "MongoDB Compass", "Cache"), "MongoDB Compass cache.");

        // Redis Insight
        AddDirectoryTarget(targets, "Cache", "Redis Insight cache", Combine(RoamingAppData, "RedisInsight", "Cache"), "Redis Insight cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetMusicProductionTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // FL Studio
        AddDirectoryTarget(targets, "Cache", "FL Studio backup", Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Documents", "Image-Line", "FL Studio", "Projects", "Backup"), "FL Studio project backups.");

        // Ableton Live
        AddDirectoryTarget(targets, "Cache", "Ableton cache", Combine(RoamingAppData, "Ableton"), "Ableton Live cache and preferences.");

        // Audacity
        AddDirectoryTarget(targets, "Cache", "Audacity temp", Combine(LocalAppData, "Audacity", "SessionData"), "Audacity session temporary files.");

        // Reaper
        AddDirectoryTarget(targets, "Cache", "Reaper peaks", Combine(RoamingAppData, "REAPER", "Peaks"), "REAPER audio peaks cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetVideoEditingTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // DaVinci Resolve
        AddDirectoryTarget(targets, "Cache", "DaVinci Resolve cache", Combine(RoamingAppData, "Blackmagic Design", "DaVinci Resolve", "Support", "CacheClip"), "DaVinci Resolve cache clips.");
        AddDirectoryTarget(targets, "Cache", "DaVinci Resolve Gallery", Combine(RoamingAppData, "Blackmagic Design", "DaVinci Resolve", "Support", "Gallery"), "DaVinci Resolve gallery stills.");

        // Premiere Pro
        AddDirectoryTarget(targets, "Cache", "Premiere Pro cache", Combine(LocalAppData, "Adobe", "Common", "Media Cache Files"), "Adobe Premiere Pro media cache.");
        AddDirectoryTarget(targets, "Cache", "After Effects cache", Combine(LocalAppData, "Adobe", "Common", "Media Cache"), "Adobe After Effects cache.");

        // OBS Studio
        AddDirectoryTarget(targets, "Logs", "OBS Studio logs", Combine(RoamingAppData, "obs-studio", "logs"), "OBS Studio recording logs.");
        AddDirectoryTarget(targets, "Cache", "OBS Studio crash reports", Combine(RoamingAppData, "obs-studio", "crashes"), "OBS Studio crash dumps.");

        // Handbrake
        AddDirectoryTarget(targets, "Logs", "Handbrake logs", Combine(RoamingAppData, "HandBrake", "logs"), "Handbrake encoding logs.");

        // Kdenlive
        AddDirectoryTarget(targets, "Cache", "Kdenlive cache", Combine(LocalAppData, "kdenlive"), "Kdenlive video editor cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetGraphicsDesignTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // GIMP
        AddDirectoryTarget(targets, "Cache", "GIMP cache", Combine(RoamingAppData, "GIMP", "2.10", "tmp"), "GIMP temporary files.");

        // Inkscape
        AddDirectoryTarget(targets, "Cache", "Inkscape cache", Combine(RoamingAppData, "inkscape"), "Inkscape cache and temp files.");

        // Paint.NET
        AddDirectoryTarget(targets, "Cache", "Paint.NET cache", Combine(LocalAppData, "paint.net"), "Paint.NET cache.");

        // Affinity Suite
        AddDirectoryTarget(targets, "Cache", "Affinity cache", Combine(RoamingAppData, "Affinity"), "Affinity Photo/Designer cache.");

        // Figma
        AddDirectoryTarget(targets, "Cache", "Figma cache", Combine(RoamingAppData, "Figma", "Cache"), "Figma desktop app cache.");
        AddDirectoryTarget(targets, "Cache", "Figma Code Cache", Combine(RoamingAppData, "Figma", "Code Cache"), "Figma JavaScript cache.");

        // Canva
        AddDirectoryTarget(targets, "Cache", "Canva cache", Combine(RoamingAppData, "Canva", "Cache"), "Canva desktop app cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> Get3DModelingTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Blender
        AddDirectoryTarget(targets, "Cache", "Blender temp", Combine(LocalAppData, "Temp", "blender_*"), "Blender temporary render files.");
        AddDirectoryTarget(targets, "Cache", "Blender cache", Combine(RoamingAppData, "Blender Foundation", "Blender"), "Blender cache and config.");

        // Unity
        AddDirectoryTarget(targets, "Cache", "Unity cache", Combine(LocalAppData, "Unity", "cache"), "Unity game engine cache.");
        AddDirectoryTarget(targets, "Logs", "Unity logs", Combine(LocalAppData, "Unity", "Editor"), "Unity Editor logs.");

        // Unreal Engine
        AddDirectoryTarget(targets, "Cache", "Unreal Engine cache", Combine(LocalAppData, "UnrealEngine"), "Unreal Engine local cache.");

        // AutoCAD
        AddDirectoryTarget(targets, "Cache", "AutoCAD cache", Combine(LocalAppData, "Autodesk"), "AutoCAD local cache.");

        // SketchUp
        AddDirectoryTarget(targets, "Cache", "SketchUp cache", Combine(LocalAppData, "SketchUp"), "SketchUp local cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetEmailClientTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Outlook
        AddDirectoryTarget(targets, "Cache", "Outlook temp attachments", Combine(LocalAppData, "Microsoft", "Windows", "INetCache", "Content.Outlook"), "Outlook temporary attachment cache.");
        AddDirectoryTarget(targets, "Cache", "Outlook AutoComplete", Combine(RoamingAppData, "Microsoft", "Outlook", "RoamCache"), "Outlook roaming cache.");

        // Thunderbird
        AddDirectoryTarget(targets, "Cache", "Thunderbird cache", Combine(LocalAppData, "Thunderbird", "Profiles"), "Thunderbird profile caches.");

        // Mailspring
        AddDirectoryTarget(targets, "Cache", "Mailspring cache", Combine(RoamingAppData, "Mailspring", "Cache"), "Mailspring email client cache.");

        // eM Client
        AddDirectoryTarget(targets, "Cache", "eM Client cache", Combine(RoamingAppData, "eM Client"), "eM Client email cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetPasswordManagerTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Note: We should NOT delete password manager data, only logs and temp files
        AddDirectoryTarget(targets, "Logs", "1Password logs", Combine(LocalAppData, "1Password", "logs"), "1Password diagnostic logs (not passwords).");
        AddDirectoryTarget(targets, "Logs", "Bitwarden logs", Combine(RoamingAppData, "Bitwarden", "logs"), "Bitwarden diagnostic logs.");
        AddDirectoryTarget(targets, "Cache", "KeePassXC cache", Combine(RoamingAppData, "KeePassXC", "cache"), "KeePassXC cache (not passwords).");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetVpnClientTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // NordVPN
        AddDirectoryTarget(targets, "Logs", "NordVPN logs", Combine(LocalAppData, "NordVPN", "Logs"), "NordVPN connection logs.");

        // ExpressVPN
        AddDirectoryTarget(targets, "Logs", "ExpressVPN logs", Combine(LocalAppData, "ExpressVPN", "Logs"), "ExpressVPN diagnostic logs.");

        // Surfshark
        AddDirectoryTarget(targets, "Logs", "Surfshark logs", Combine(LocalAppData, "Surfshark"), "Surfshark VPN logs.");

        // ProtonVPN
        AddDirectoryTarget(targets, "Logs", "ProtonVPN logs", Combine(LocalAppData, "ProtonVPN", "Logs"), "ProtonVPN connection logs.");

        // OpenVPN
        AddDirectoryTarget(targets, "Logs", "OpenVPN logs", Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "OpenVPN", "log"), "OpenVPN connection logs.");

        // WireGuard
        AddDirectoryTarget(targets, "Logs", "WireGuard logs", Combine(ProgramData, "WireGuard", "log"), "WireGuard logs.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetAntivirusTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Malwarebytes
        AddDirectoryTarget(targets, "Logs", "Malwarebytes logs", Combine(ProgramData, "Malwarebytes", "Malwarebytes Anti-Malware", "Logs"), "Malwarebytes scan logs.");

        // Avast
        AddDirectoryTarget(targets, "Cache", "Avast cache", Combine(ProgramData, "AVAST Software", "Avast", "chest"), "Avast virus chest (quarantine).");
        AddDirectoryTarget(targets, "Logs", "Avast logs", Combine(ProgramData, "AVAST Software", "Avast", "log"), "Avast diagnostic logs.");

        // AVG
        AddDirectoryTarget(targets, "Logs", "AVG logs", Combine(ProgramData, "AVG", "Antivirus", "log"), "AVG antivirus logs.");

        // Bitdefender
        AddDirectoryTarget(targets, "Logs", "Bitdefender logs", Combine(ProgramData, "Bitdefender", "Desktop", "Profiles", "Logs"), "Bitdefender scan logs.");

        // Kaspersky
        AddDirectoryTarget(targets, "Logs", "Kaspersky logs", Combine(ProgramData, "Kaspersky Lab"), "Kaspersky security logs.");

        // ESET
        AddDirectoryTarget(targets, "Logs", "ESET logs", Combine(ProgramData, "ESET", "ESET Security", "Logs"), "ESET NOD32 logs.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetSystemToolTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // CCleaner (ironic but true)
        AddDirectoryTarget(targets, "Logs", "CCleaner logs", Combine(ProgramData, "Piriform", "CCleaner"), "CCleaner logs and data.");

        // TreeSize
        AddDirectoryTarget(targets, "Cache", "TreeSize cache", Combine(LocalAppData, "JAM Software", "TreeSize"), "TreeSize scan cache.");

        // Everything
        AddDirectoryTarget(targets, "Cache", "Everything index", Combine(LocalAppData, "Everything"), "Everything search index (rebuilds quickly).");

        // Process Monitor
        AddDirectoryTarget(targets, "Logs", "Procmon logs", Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Documents", "Procmon*.pml"), "Process Monitor capture files.");

        // Wireshark
        AddDirectoryTarget(targets, "Cache", "Wireshark temp", Combine(RoamingAppData, "Wireshark"), "Wireshark preferences (not captures).");

        // Sysinternals
        AddDirectoryTarget(targets, "Cache", "Sysinternals", Combine(RoamingAppData, "Sysinternals"), "Sysinternals tools settings.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetWindowsServiceCacheTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Windows Installer
        AddDirectoryTarget(targets, "Cache", "Windows Installer cache", Combine(WindowsDirectory, "Installer"), "Windows Installer cached MSI files. Warning: May break uninstall.");

        // Group Policy cache
        AddDirectoryTarget(targets, "Cache", "Group Policy cache", Combine(LocalAppData, "Microsoft", "Group Policy"), "Local Group Policy cache.");

        // Cortana data
        AddDirectoryTarget(targets, "Cache", "Cortana data local", Combine(LocalAppData, "Microsoft", "Cortana"), "Cortana local data.");

        // Windows Apps shared storage
        foreach (var packageTemp in EnumeratePackageTempFolders("Microsoft.Windows"))
        {
            AddDirectoryTarget(targets, "Cache", $"{packageTemp.Label} temp", packageTemp.Path, "Windows app temporary files.");
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetMsixAndAppxTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        var packagesRoot = Path.Combine(LocalAppData, "Packages");
        if (!Directory.Exists(packagesRoot))
        {
            return targets;
        }

        // Get all package AC/Temp folders
        foreach (var pkg in SafeEnumerateDirectories(packagesRoot))
        {
            var name = Path.GetFileName(pkg);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Skip known packages we already handle specifically
            if (name.StartsWith("Microsoft.Teams", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Microsoft.Xbox", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Microsoft.GamingApp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tempPath = Path.Combine(pkg, "AC", "Temp");
            if (Directory.Exists(tempPath))
            {
                AddDirectoryTarget(targets, "Cache", $"{name} temp", tempPath, "UWP/MSIX app temporary files.");
            }

            var inetCache = Path.Combine(pkg, "AC", "INetCache");
            if (Directory.Exists(inetCache))
            {
                AddDirectoryTarget(targets, "Cache", $"{name} INetCache", inetCache, "UWP/MSIX app internet cache.");
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetNetworkCacheTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // DNS cache is in memory, but we can note hosts file
        AddDirectoryTarget(targets, "Cache", "Offline web pages", Combine(LocalAppData, "Microsoft", "Windows", "Offline Web Pages"), "Saved offline web pages.");

        // Credential cache
        AddDirectoryTarget(targets, "Cache", "Web credentials temp", Combine(LocalAppData, "Microsoft", "Credentials"), "Cached web credentials temp (not the actual credentials).");

        // Network cache (Cookies, etc.)
        AddDirectoryTarget(targets, "History", "Cookie data", Combine(LocalAppData, "Microsoft", "Windows", "INetCookies"), "Internet cookie cache.");

        // WebCache
        AddDirectoryTarget(targets, "Cache", "WebCache container", Combine(LocalAppData, "Microsoft", "Windows", "WebCache"), "Windows WebCache database for IE/Edge Legacy.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetOtherTempLocations()
    {
        var targets = new List<CleanupTargetDefinition>();
        var systemDrive = GetSystemDrive();

        // Root temp folders sometimes created by installers
        AddDirectoryTarget(targets, "Temp", "Root TEMP folder", Path.Combine(systemDrive, "TEMP"), "Root level TEMP folder created by some installers.");
        AddDirectoryTarget(targets, "Temp", "Root Tmp folder", Path.Combine(systemDrive, "tmp"), "Root level tmp folder.");

        // Intel/AMD installer residue
        AddDirectoryTarget(targets, "Installer", "Intel driver temp", Path.Combine(systemDrive, "Intel"), "Intel driver installer temp files.");
        AddDirectoryTarget(targets, "Installer", "AMD driver temp", Path.Combine(systemDrive, "AMD"), "AMD driver installer temp files.");
        AddDirectoryTarget(targets, "Installer", "NVIDIA driver temp", Path.Combine(systemDrive, "NVIDIA"), "NVIDIA driver installer temp files.");

        // MSI extracted files
        AddDirectoryTarget(targets, "Installer", "MSI temp extract", Path.Combine(systemDrive, "MSOCache"), "Microsoft Office installer cache.");
        AddDirectoryTarget(targets, "Installer", "HP temp", Path.Combine(systemDrive, "SWSetup"), "HP software setup files.");
        AddDirectoryTarget(targets, "Installer", "Dell temp", Path.Combine(systemDrive, "Dell"), "Dell installer files.");
        AddDirectoryTarget(targets, "Installer", "Lenovo temp", Path.Combine(systemDrive, "Lenovo"), "Lenovo installer files.");
        AddDirectoryTarget(targets, "Installer", "ASUS temp", Path.Combine(systemDrive, "eSupport"), "ASUS support files.");

        // Recovery partition files (on C:)
        AddDirectoryTarget(targets, "Installer", "Recovery staging", Path.Combine(systemDrive, "Recovery"), "Windows Recovery staging folder.");

        // Perflogs
        AddDirectoryTarget(targets, "Logs", "Performance logs", Path.Combine(systemDrive, "PerfLogs"), "Windows Performance Monitor logs.");

        // Windows old backup
        AddDirectoryTarget(targets, "Orphaned", "Windows.old.000", Path.Combine(systemDrive, "Windows.old.000"), "Additional Windows.old backup folder.");

        // Hidden installer caches
        AddDirectoryTarget(targets, "Installer", "Config.Msi temp", Combine(WindowsDirectory, "Installer", "Config.Msi"), "Windows Installer rollback files.");

        // Thumbnail database in user folders
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddFileTarget(targets, "Cache", "Desktop thumbnail DB", Path.Combine(userProfile, "Desktop", "Thumbs.db"), "Legacy thumbnail database.");
            AddFileTarget(targets, "Cache", "Documents thumbnail DB", Path.Combine(userProfile, "Documents", "Thumbs.db"), "Legacy thumbnail database.");
            AddFileTarget(targets, "Cache", "Pictures thumbnail DB", Path.Combine(userProfile, "Pictures", "Thumbs.db"), "Legacy thumbnail database.");
            AddFileTarget(targets, "Cache", "Downloads thumbnail DB", Path.Combine(userProfile, "Downloads", "Thumbs.db"), "Legacy thumbnail database.");
            AddFileTarget(targets, "Cache", "Videos thumbnail DB", Path.Combine(userProfile, "Videos", "Thumbs.db"), "Legacy thumbnail database.");
        }

        // CryptnetUrlCache
        AddDirectoryTarget(targets, "Cache", "Crypto URL cache", Combine(LocalAppData, "Microsoft", "Windows", "INetCache", "IE"), "Cryptographic certificate URL cache.");

        // Diagnostic data
        AddDirectoryTarget(targets, "Logs", "Diagnostic reports", Combine(ProgramData, "Microsoft", "Windows", "SystemData"), "System diagnostic data.");

        // Connected Devices Platform
        AddDirectoryTarget(targets, "Cache", "Connected Devices cache", Combine(LocalAppData, "ConnectedDevicesPlatform"), "Windows Connected Devices Platform cache.");

        // Windows Notifications
        AddDirectoryTarget(targets, "Cache", "Notifications DB", Combine(LocalAppData, "Microsoft", "Windows", "Notifications"), "Windows notification database.");

        // Action Center
        AddDirectoryTarget(targets, "Cache", "Action Center cache", Combine(LocalAppData, "Microsoft", "Windows", "ActionCenterCache"), "Windows Action Center cache.");

        // Start Menu cache
        AddDirectoryTarget(targets, "Cache", "Start Menu cache", Combine(LocalAppData, "Microsoft", "Windows", "Caches"), "Windows Start Menu caches.");

        // Windows Feedback
        AddDirectoryTarget(targets, "Logs", "Feedback Hub", Combine(LocalAppData, "Packages", "Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe", "LocalState"), "Windows Feedback Hub data.");

        // Settings app cache
        AddDirectoryTarget(targets, "Cache", "Settings app cache", Combine(LocalAppData, "Packages", "windows.immersivecontrolpanel_cw5n1h2txyewy", "LocalCache"), "Settings app cache.");

        // Game bar captures (optional - might want to keep)
        AddDirectoryTarget(targets, "Cache", "Game Bar captures", Combine(LocalAppData, "Packages", "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe", "LocalState", "GameDVR"), "Xbox Game Bar video captures.");

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

    private static IEnumerable<CleanupTargetDefinition> GetPackageManagerCacheTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Chocolatey
        AddDirectoryTarget(targets, "Cache", "Chocolatey cache", Combine(ProgramData, "chocolatey", ".cache"), "Chocolatey package cache.");
        AddDirectoryTarget(targets, "Cache", "Chocolatey temp", Combine(ProgramData, "chocolatey", "temp"), "Chocolatey temporary files.");
        AddDirectoryTarget(targets, "Logs", "Chocolatey logs", Combine(ProgramData, "chocolatey", "logs"), "Chocolatey installation logs.");

        // Scoop
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddDirectoryTarget(targets, "Cache", "Scoop cache", Path.Combine(userProfile, "scoop", "cache"), "Scoop package download cache.");
            AddDirectoryTarget(targets, "Cache", "Scoop apps cache", Path.Combine(userProfile, "scoop", "apps", "scoop", "current", "cache"), "Scoop apps cache.");
        }

        // Winget
        AddDirectoryTarget(targets, "Cache", "Winget packages", Combine(LocalAppData, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState"), "Winget state and logs.");
        AddDirectoryTarget(targets, "Logs", "Winget logs", Combine(LocalAppData, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "DiagOutputDir"), "Winget diagnostic logs.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetAdditionalGameLauncherTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // GOG Galaxy
        AddDirectoryTarget(targets, "Cache", "GOG Galaxy webcache", Combine(LocalAppData, "GOG.com", "Galaxy", "webcache"), "GOG Galaxy web cache.");
        AddDirectoryTarget(targets, "Logs", "GOG Galaxy logs", Combine(ProgramData, "GOG.com", "Galaxy", "logs"), "GOG Galaxy logs.");

        // Ubisoft Connect
        AddDirectoryTarget(targets, "Cache", "Ubisoft Connect cache", Combine(LocalAppData, "Ubisoft Game Launcher", "cache"), "Ubisoft Connect cache.");
        AddDirectoryTarget(targets, "Logs", "Ubisoft Connect logs", Combine(LocalAppData, "Ubisoft Game Launcher", "logs"), "Ubisoft Connect logs.");

        // EA App (formerly Origin)
        AddDirectoryTarget(targets, "Cache", "EA App cache", Combine(LocalAppData, "Electronic Arts", "EA Desktop", "cache"), "EA Desktop app cache.");
        AddDirectoryTarget(targets, "Logs", "EA App logs", Combine(ProgramData, "Electronic Arts", "EA Desktop", "Logs"), "EA Desktop logs.");
        AddDirectoryTarget(targets, "Cache", "Origin cache", Combine(LocalAppData, "Origin", "cache"), "Origin launcher cache (legacy).");
        AddDirectoryTarget(targets, "Logs", "Origin logs", Combine(ProgramData, "Origin", "Logs"), "Origin logs (legacy).");

        // Battle.net
        AddDirectoryTarget(targets, "Cache", "Battle.net cache", Combine(ProgramData, "Blizzard Entertainment", "Battle.net", "Cache"), "Battle.net launcher cache.");
        AddDirectoryTarget(targets, "Logs", "Battle.net logs", Combine(ProgramData, "Blizzard Entertainment", "Battle.net", "Logs"), "Battle.net logs.");

        // Rockstar Games Launcher
        AddDirectoryTarget(targets, "Cache", "Rockstar cache", Combine(LocalAppData, "Rockstar Games", "Launcher"), "Rockstar Games Launcher cache.");

        // Riot Client (League of Legends, Valorant)
        AddDirectoryTarget(targets, "Logs", "Riot Client logs", Combine(LocalAppData, "Riot Games", "Riot Client", "Logs"), "Riot Client logs.");
        AddDirectoryTarget(targets, "Cache", "Riot Client data", Combine(LocalAppData, "Riot Games", "Riot Client", "Data"), "Riot Client data cache.");

        // Bethesda.net Launcher
        AddDirectoryTarget(targets, "Cache", "Bethesda.net cache", Combine(LocalAppData, "Bethesda.net Launcher"), "Bethesda.net launcher cache.");

        // Amazon Games
        AddDirectoryTarget(targets, "Cache", "Amazon Games cache", Combine(LocalAppData, "Amazon Games"), "Amazon Games app cache.");

        // itch.io
        AddDirectoryTarget(targets, "Cache", "itch.io cache", Combine(RoamingAppData, "itch"), "itch.io app cache.");

        // Humble Bundle
        AddDirectoryTarget(targets, "Cache", "Humble App cache", Combine(LocalAppData, "Humble App"), "Humble Bundle app cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetRemoteAccessToolTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // TeamViewer
        AddDirectoryTarget(targets, "Logs", "TeamViewer logs", Combine(RoamingAppData, "TeamViewer"), "TeamViewer connection logs.");

        // AnyDesk
        AddDirectoryTarget(targets, "Logs", "AnyDesk logs", Combine(RoamingAppData, "AnyDesk"), "AnyDesk session logs.");
        AddDirectoryTarget(targets, "Cache", "AnyDesk thumbnails", Combine(ProgramData, "AnyDesk", "thumbnails"), "AnyDesk thumbnail cache.");

        // Parsec
        AddDirectoryTarget(targets, "Logs", "Parsec logs", Combine(RoamingAppData, "Parsec", "log"), "Parsec streaming logs.");

        // Chrome Remote Desktop
        AddDirectoryTarget(targets, "Logs", "Chrome Remote Desktop logs", Combine(LocalAppData, "Google", "Chrome Remote Desktop", "Logs"), "Chrome Remote Desktop logs.");

        // RustDesk
        AddDirectoryTarget(targets, "Logs", "RustDesk logs", Combine(RoamingAppData, "RustDesk", "logs"), "RustDesk connection logs.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetStreamingAppTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Netflix (UWP)
        AddDirectoryTarget(targets, "Cache", "Netflix cache", Combine(LocalAppData, "Packages", "4DF9E0F8.Netflix_mcm4njqhnhss8", "LocalCache"), "Netflix app cache.");

        // Disney+ (UWP)
        AddDirectoryTarget(targets, "Cache", "Disney+ cache", Combine(LocalAppData, "Packages", "Disney.37853FC22B2CE_6rarf9sa4v8jt", "LocalCache"), "Disney+ app cache.");

        // Amazon Prime Video (UWP)
        AddDirectoryTarget(targets, "Cache", "Prime Video cache", Combine(LocalAppData, "Packages", "AmazonVideo.PrimeVideo_pwbj9vvecjh7j", "LocalCache"), "Amazon Prime Video cache.");

        // Twitch
        AddDirectoryTarget(targets, "Cache", "Twitch cache", Combine(RoamingAppData, "Twitch", "Cache"), "Twitch desktop app cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetNoteTakingAppTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Notion
        AddDirectoryTarget(targets, "Cache", "Notion cache", Combine(RoamingAppData, "Notion", "Cache"), "Notion desktop cache.");
        AddDirectoryTarget(targets, "Cache", "Notion Code Cache", Combine(RoamingAppData, "Notion", "Code Cache"), "Notion JavaScript cache.");

        // Obsidian
        AddDirectoryTarget(targets, "Cache", "Obsidian cache", Combine(RoamingAppData, "obsidian", "Cache"), "Obsidian cache.");
        AddDirectoryTarget(targets, "Cache", "Obsidian GPU cache", Combine(RoamingAppData, "obsidian", "GPUCache"), "Obsidian GPU cache.");

        // Logseq
        AddDirectoryTarget(targets, "Cache", "Logseq cache", Combine(RoamingAppData, "Logseq", "Cache"), "Logseq cache.");

        // Joplin
        AddDirectoryTarget(targets, "Cache", "Joplin cache", Combine(RoamingAppData, "joplin-desktop", "cache"), "Joplin note app cache.");

        // Standard Notes
        AddDirectoryTarget(targets, "Cache", "Standard Notes cache", Combine(RoamingAppData, "Standard Notes", "Cache"), "Standard Notes cache.");

        // Evernote
        AddDirectoryTarget(targets, "Cache", "Evernote cache", Combine(LocalAppData, "Evernote", "Evernote", "cache"), "Evernote desktop cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetScreenshotToolTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // ShareX
        AddDirectoryTarget(targets, "Logs", "ShareX logs", Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Documents", "ShareX", "Logs"), "ShareX operation logs.");

        // Greenshot
        AddDirectoryTarget(targets, "Cache", "Greenshot temp", Combine(RoamingAppData, "Greenshot"), "Greenshot settings and temp.");

        // Lightshot
        AddDirectoryTarget(targets, "Cache", "Lightshot cache", Combine(RoamingAppData, "Skillbrains", "lightshot"), "Lightshot cache.");

        // Snagit
        AddDirectoryTarget(targets, "Cache", "Snagit temp", Combine(LocalAppData, "TechSmith", "Snagit", "Autosave"), "Snagit autosave files.");
        AddDirectoryTarget(targets, "Cache", "Snagit Library cache", Combine(LocalAppData, "TechSmith", "Snagit", "LibraryCache"), "Snagit library cache.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetWebView2Targets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // Edge WebView2 runtime cache (used by many Electron and native apps)
        foreach (var app in SafeEnumerateDirectories(LocalAppData))
        {
            var webview = Path.Combine(app, "EBWebView");
            if (!Directory.Exists(webview))
            {
                continue;
            }

            var appName = Path.GetFileName(app);
            if (string.IsNullOrWhiteSpace(appName) || appName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cache = Path.Combine(webview, "Default", "Cache");
            if (Directory.Exists(cache))
            {
                AddDirectoryTarget(targets, "Cache", $"{appName} WebView2 cache", cache, "WebView2 embedded browser cache.");
            }
        }

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetPowerShellTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            // PowerShell history
            AddFileTarget(targets, "History", "PowerShell history", Path.Combine(userProfile, "AppData", "Roaming", "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt"), "PowerShell command history.");

            // PowerShell module cache
            AddDirectoryTarget(targets, "Cache", "PowerShell module cache", Path.Combine(userProfile, ".local", "share", "powershell", "ModuleAnalysisCache"), "PowerShell module analysis cache.");
        }

        // PowerShell logs
        AddDirectoryTarget(targets, "Logs", "PowerShell logs", Combine(LocalAppData, "Microsoft", "Windows", "PowerShell", "Logs"), "PowerShell operational logs.");

        return targets;
    }

    private static IEnumerable<CleanupTargetDefinition> GetWindowsSubsystemTargets()
    {
        var targets = new List<CleanupTargetDefinition>();

        // WSL temp files
        AddDirectoryTarget(targets, "Cache", "WSL temp", Combine(LocalAppData, "Temp", "wsl"), "Windows Subsystem for Linux temp files.");
        AddDirectoryTarget(targets, "Cache", "WSL logs", Combine(LocalAppData, "WSL", "logs"), "WSL diagnostic logs.");

        // WSA (Windows Subsystem for Android)
        AddDirectoryTarget(targets, "Cache", "WSA temp", Combine(LocalAppData, "Packages", "MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe", "LocalCache"), "Windows Subsystem for Android cache.");

        // Phone Link / Your Phone
        AddDirectoryTarget(targets, "Cache", "Phone Link cache", Combine(LocalAppData, "Packages", "Microsoft.YourPhone_8wekyb3d8bbwe", "LocalCache"), "Phone Link (Your Phone) app cache.");
        AddDirectoryTarget(targets, "Cache", "Phone Link temp", Combine(LocalAppData, "Packages", "Microsoft.YourPhone_8wekyb3d8bbwe", "TempState"), "Phone Link temp state.");

        // Dev Home (Windows 11)
        AddDirectoryTarget(targets, "Cache", "Dev Home cache", Combine(LocalAppData, "Packages", "Microsoft.DevHome_8wekyb3d8bbwe", "LocalCache"), "Dev Home app cache.");

        // Windows Sandbox
        AddDirectoryTarget(targets, "Cache", "Windows Sandbox", Combine(ProgramData, "Microsoft", "Windows", "Containers", "Sandboxes"), "Windows Sandbox container files.");

        // Hyper-V
        AddDirectoryTarget(targets, "Cache", "Hyper-V cache", Combine(ProgramData, "Microsoft", "Windows", "Hyper-V", "Resource Types"), "Hyper-V resource cache.");

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
