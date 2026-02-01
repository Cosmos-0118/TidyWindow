using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Centralized, conservative classification for system-critical paths used by cleanup flows.
/// Only blocks locations whose removal is likely to break Windows, drivers, or boot.
/// Explicitly allows known-safe cleanup locations even if they live under protected parent folders.
/// </summary>
public static class CleanupSystemPathSafety
{
    private static readonly Lazy<HashSet<string>> CriticalRoots = new(() => BuildCriticalRoots(), isThreadSafe: true);
    private static readonly Lazy<HashSet<string>> SafeCleanupPaths = new(() => BuildSafeCleanupPaths(), isThreadSafe: true);
    private static readonly ConcurrentDictionary<string, byte> AdditionalRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Critical boot and system files that should NEVER be deleted regardless of location.
    /// </summary>
    private static readonly HashSet<string> CriticalFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Boot files
        "bootmgr",
        "bootmgr.efi",
        "bootnxt",
        "bcd",
        "bcd.log",
        "bcd.log1",
        "bcd.log2",
        
        // Hibernation and paging
        "hiberfil.sys",
        "pagefile.sys",
        "swapfile.sys",
        
        // Windows boot loaders
        "winresume.exe",
        "winresume.efi",
        "winload.exe",
        "winload.efi",
        
        // Kernel and HAL
        "ntoskrnl.exe",
        "hal.dll",
        "ntdll.dll",
        "kernel32.dll",
        "kernelbase.dll",
        
        // Registry hives
        "sam",
        "security",
        "software",
        "system",
        "default",
        "ntuser.dat",
        "usrclass.dat"
    };

    /// <summary>
    /// Returns true when the path points to a Windows- or boot-critical file or directory tree.
    /// Returns false for known-safe cleanup locations even if under a protected parent.
    /// </summary>
    public static bool IsSystemCriticalPath(string? path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        }

        if (path.IndexOfAny(new[] { '<', '>', '|', '"' }) >= 0)
        {
            throw new ArgumentException("The provided path contains invalid characters.", nameof(path));
        }

        var trimmed = EnsureDriveColonIfMissing(path.Trim());

        // Reject relative or drive-relative paths as non-critical.
        if (!Path.IsPathRooted(trimmed))
        {
            return false;
        }

        var driveRoot = Path.GetPathRoot(trimmed) ?? string.Empty;
        var isDriveRelative = driveRoot.Length == 2 && (trimmed.Length == 2 || (trimmed.Length > 2 && trimmed[2] != Path.DirectorySeparatorChar && trimmed[2] != Path.AltDirectorySeparatorChar));
        if (isDriveRelative)
        {
            return false;
        }

        var normalized = Normalize(trimmed);
        if (normalized.Length == 0)
        {
            return false;
        }

        // Always protect critical files regardless of location
        if (IsCriticalFile(normalized))
        {
            return true;
        }

        // FIRST: Check if this is a known-safe cleanup path - these are ALWAYS allowed
        foreach (var safePath in SafeCleanupPaths.Value)
        {
            if (IsSameOrSubPath(normalized, safePath))
            {
                return false; // Explicitly safe to clean
            }
        }

        // THEN: Check if it's under a critical root
        foreach (var root in CriticalRoots.Value)
        {
            if (IsSameOrSubPath(normalized, root))
            {
                return true;
            }
        }

        foreach (var extra in AdditionalRoots.Keys)
        {
            if (IsSameOrSubPath(normalized, extra))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds additional protected roots at runtime (e.g., enterprise policies).
    /// </summary>
    public static void SetAdditionalCriticalRoots(IEnumerable<string>? roots)
    {
        AdditionalRoots.Clear();
        if (roots is null)
        {
            return;
        }

        foreach (var root in roots)
        {
            var normalized = Normalize(root, ensureTrailingSeparator: true);
            if (normalized.Length == 0)
            {
                continue;
            }

            AdditionalRoots.TryAdd(normalized, 0);
        }
    }

    private static bool IsCriticalFile(string normalizedPath)
    {
        var fileName = Path.GetFileName(normalizedPath);
        if (fileName.Length == 0)
        {
            return false;
        }

        return CriticalFiles.Contains(fileName);
    }

    /// <summary>
    /// Builds the set of known-safe cleanup locations that should NEVER be blocked,
    /// even if they are under a protected parent folder like C:\Windows.
    /// </summary>
    private static HashSet<string> BuildSafeCleanupPaths()
    {
        var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSafe(string? candidate)
        {
            var normalized = Normalize(candidate, ensureTrailingSeparator: true);
            if (normalized.Length > 0)
            {
                safe.Add(normalized);
            }
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            // Windows Temp - always safe
            AddSafe(Path.Combine(windows, "Temp"));

            // Windows Prefetch - safe, regenerated automatically
            AddSafe(Path.Combine(windows, "Prefetch"));

            // Windows Update downloads - safe to clean
            AddSafe(Path.Combine(windows, "SoftwareDistribution", "Download"));
            AddSafe(Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"));

            // Windows logs - safe to clean (but not critical CBS logs during servicing)
            AddSafe(Path.Combine(windows, "Logs", "WindowsUpdate"));
            AddSafe(Path.Combine(windows, "Logs", "CBS"));
            AddSafe(Path.Combine(windows, "Logs", "DISM"));
            AddSafe(Path.Combine(windows, "Logs", "MoSetup"));
            AddSafe(Path.Combine(windows, "Logs", "DPX"));
            AddSafe(Path.Combine(windows, "Logs", "SIH"));

            // Windows Installer patch cache - safe to clean
            AddSafe(Path.Combine(windows, "Installer", "$PatchCache$"));
            AddSafe(Path.Combine(windows, "Installer", "Config.Msi"));

            // Panther setup logs - safe
            AddSafe(Path.Combine(windows, "Panther"));

            // Minidump crash files - safe
            AddSafe(Path.Combine(windows, "Minidump"));
            AddSafe(Path.Combine(windows, "LiveKernelReports"));

            // Font cache - safe, regenerates
            AddSafe(Path.Combine(windows, "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache"));

            // Downloaded Program Files (legacy ActiveX) - safe
            AddSafe(Path.Combine(windows, "Downloaded Program Files"));

            // Debug files - safe
            AddSafe(Path.Combine(windows, "Debug"));

            // Memory dumps - safe  
            AddSafe(Path.Combine(windows, "memory.dmp"));

            // AppCompat cache - safe
            AddSafe(Path.Combine(windows, "appcompat", "Programs", "Install"));

            // Defender scan results and temp (not the program itself)
            AddSafe(Path.Combine(windows, "Temp", "MpCmdRun.log"));
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            // Windows Error Reporting - safe
            AddSafe(Path.Combine(programData, "Microsoft", "Windows", "WER"));

            // USO Update Store - safe
            AddSafe(Path.Combine(programData, "USOPrivate", "UpdateStore"));
            AddSafe(Path.Combine(programData, "USOShared", "Logs"));

            // Package cache - safe (installer caches)
            AddSafe(Path.Combine(programData, "Package Cache"));

            // Microsoft Crypto (only the Keys folder is critical, logs are safe)
            AddSafe(Path.Combine(programData, "Microsoft", "Crypto", "RSA", "MachineKeys")); // Temp cert files

            // Windows Defender - scans and temp files are safe, but not the program
            AddSafe(Path.Combine(programData, "Microsoft", "Windows Defender", "Scans", "History"));
            AddSafe(Path.Combine(programData, "Microsoft", "Windows Defender", "Support"));

            // Network cache
            AddSafe(Path.Combine(programData, "Microsoft", "Network", "Downloader"));

            // Search index - can be rebuilt
            AddSafe(Path.Combine(programData, "Microsoft", "Search", "Data"));
        }

        return safe;
    }

    private static HashSet<string> BuildCriticalRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            var normalized = Normalize(candidate, ensureTrailingSeparator: true);
            if (normalized.Length == 0)
            {
                return;
            }

            roots.Add(normalized);
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            // Core OS binaries and components - CRITICAL
            Add(Path.Combine(windows, "System32"));
            Add(Path.Combine(windows, "SysWOW64"));
            Add(Path.Combine(windows, "SysArm32"));
            Add(Path.Combine(windows, "WinSxS"));
            Add(Path.Combine(windows, "SystemApps"));
            Add(Path.Combine(windows, "SystemResources"));
            Add(Path.Combine(windows, "servicing"));
            Add(Path.Combine(windows, "assembly"));
            Add(Path.Combine(windows, "Fonts"));
            Add(Path.Combine(windows, "INF"));
            Add(Path.Combine(windows, "Globalization"));
            Add(Path.Combine(windows, "Boot"));
            Add(Path.Combine(windows, "Branding"));
            Add(Path.Combine(windows, "diagnostics"));
            Add(Path.Combine(windows, "Help"));
            Add(Path.Combine(windows, "ImmersiveControlPanel"));
            Add(Path.Combine(windows, "InputMethod"));
            Add(Path.Combine(windows, "L2Schemas"));
            Add(Path.Combine(windows, "Cursors"));
            Add(Path.Combine(windows, "Media"));
            Add(Path.Combine(windows, "Migration"));
            Add(Path.Combine(windows, "PolicyDefinitions"));
            Add(Path.Combine(windows, "PrintDialog"));
            Add(Path.Combine(windows, "rescache"));
            Add(Path.Combine(windows, "Resources"));
            Add(Path.Combine(windows, "schemas"));
            Add(Path.Combine(windows, "Security"));
            Add(Path.Combine(windows, "ShellComponents"));
            Add(Path.Combine(windows, "ShellExperiences"));
            Add(Path.Combine(windows, "SKB"));
            Add(Path.Combine(windows, "Speech"));
            Add(Path.Combine(windows, "Speech_OneCore"));
            Add(Path.Combine(windows, "System"));
            Add(Path.Combine(windows, "TextInput"));
            Add(Path.Combine(windows, "tracing"));
            Add(Path.Combine(windows, "Web"));
            Add(Path.Combine(windows, "WaaS"));

            // Note: We intentionally do NOT add C:\Windows itself or C:\Windows\Installer
            // because safe cleanup paths within them are explicitly allowed
        }

        // Boot and recovery partitions - CRITICAL
        var systemDrive = !string.IsNullOrWhiteSpace(windows) ? Path.GetPathRoot(windows) : Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(systemDrive))
        {
            Add(Path.Combine(systemDrive, "Boot"));
            Add(Path.Combine(systemDrive, "EFI"));
            Add(Path.Combine(systemDrive, "Recovery"));
            Add(Path.Combine(systemDrive, "System Volume Information"));
            Add(Path.Combine(systemDrive, "bootmgr"));
        }

        // Windows Apps (MSIX/UWP core)
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            Add(Path.Combine(programFiles, "WindowsApps"));
            Add(Path.Combine(programFiles, "Windows Defender"));
            Add(Path.Combine(programFiles, "Windows Security"));
            Add(Path.Combine(programFiles, "Windows NT"));
            Add(Path.Combine(programFiles, "Windows Photo Viewer"));
            Add(Path.Combine(programFiles, "Windows Portable Devices"));
            Add(Path.Combine(programFiles, "Windows Sidebar"));
            Add(Path.Combine(programFiles, "Windows Mail"));
            Add(Path.Combine(programFiles, "Windows Media Player"));
            Add(Path.Combine(programFiles, "Reference Assemblies", "Microsoft"));
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86) && !programFilesX86.Equals(programFiles, StringComparison.OrdinalIgnoreCase))
        {
            Add(Path.Combine(programFilesX86, "WindowsApps"));
            Add(Path.Combine(programFilesX86, "Windows Defender"));
            Add(Path.Combine(programFilesX86, "Windows Security"));
            Add(Path.Combine(programFilesX86, "Windows NT"));
            Add(Path.Combine(programFilesX86, "Windows Photo Viewer"));
            Add(Path.Combine(programFilesX86, "Windows Portable Devices"));
            Add(Path.Combine(programFilesX86, "Windows Sidebar"));
            Add(Path.Combine(programFilesX86, "Windows Mail"));
            Add(Path.Combine(programFilesX86, "Windows Media Player"));
            Add(Path.Combine(programFilesX86, "Reference Assemblies", "Microsoft"));
        }

        // Core system data - CRITICAL (but NOT the entire ProgramData\Microsoft\Windows folder)
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            // Crypto and protection stores - CRITICAL
            Add(Path.Combine(programData, "Microsoft", "Crypto"));
            Add(Path.Combine(programData, "Microsoft", "Protect"));

            // Windows Defender program data (not logs/scans)
            Add(Path.Combine(programData, "Microsoft", "Windows Defender", "Definition Updates"));
            Add(Path.Combine(programData, "Microsoft", "Windows Defender", "Platform"));

            // Group Policy - CRITICAL
            Add(Path.Combine(programData, "Microsoft", "Group Policy"));

            // Device drivers
            Add(Path.Combine(programData, "Microsoft", "Windows", "DeviceSetupManager"));

            // ClickToRun
            Add(Path.Combine(programData, "Microsoft", "ClickToRun"));
        }

        // User profile folders that should never be bulk-deleted
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            // NTUSER.DAT location
            Add(Path.Combine(userProfile, "NTUSER.DAT"));
        }

        return roots;
    }

    private static string Normalize(string? path, bool ensureTrailingSeparator = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (ContainsInvalidChars(path))
        {
            throw new ArgumentException("The provided path contains invalid characters.", nameof(path));
        }

        try
        {
            var trimmed = path.Trim().Trim('"');
            var expanded = Environment.ExpandEnvironmentVariables(trimmed);
            var basePath = Environment.SystemDirectory;
            var full = Path.GetFullPath(expanded, string.IsNullOrWhiteSpace(basePath) ? Directory.GetCurrentDirectory() : basePath);
            var roundTrip = Path.GetFullPath(full);
            if (!string.Equals(full, roundTrip, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return NormalizeAsDirectory(full, ensureTrailingSeparator);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The provided path is invalid.", nameof(path), ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ArgumentException("The provided path uses an unsupported format.", nameof(path), ex);
        }
        catch (SecurityException ex)
        {
            throw new ArgumentException("Access to the provided path was denied.", nameof(path), ex);
        }
    }

    private static string NormalizeAsDirectory(string path, bool ensureTrailingSeparator)
    {
        if (!ensureTrailingSeparator)
        {
            return path;
        }

        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool IsSameOrSubPath(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, candidate);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return true;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        return !Path.IsPathRooted(relative);
    }

    private static bool ContainsTraversal(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        return original.Contains("..", StringComparison.Ordinal);
    }

    private static bool ContainsInvalidChars(string candidate)
    {
        var invalid = Path.GetInvalidPathChars();
        if (candidate.IndexOfAny(invalid) >= 0)
        {
            return true;
        }

        return candidate.IndexOfAny(new[] { '<', '>', '|', '"' }) >= 0;
    }

    private static string EnsureDriveColonIfMissing(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 2)
        {
            return candidate;
        }

        var first = candidate[0];
        var second = candidate[1];
        var isDriveLetter = (first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z');
        var isSeparator = second == Path.DirectorySeparatorChar || second == Path.AltDirectorySeparatorChar;
        if (isDriveLetter && isSeparator && candidate.Length > 2 && candidate[2] != Path.VolumeSeparatorChar)
        {
            return string.Concat(first, Path.VolumeSeparatorChar, candidate.Substring(1));
        }

        return candidate;
    }
}
