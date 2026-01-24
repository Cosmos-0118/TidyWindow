using System;
using System.Collections.Generic;
using System.IO;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Centralized, conservative classification for system-critical paths used by cleanup flows.
/// Only blocks locations whose removal is likely to break Windows, drivers, or boot.
/// </summary>
public static class CleanupSystemPathSafety
{
    private static readonly Lazy<HashSet<string>> CriticalRoots = new(() => BuildCriticalRoots(), isThreadSafe: true);
    private static readonly HashSet<string> CriticalFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "bootmgr",
        "bootnxt",
        "bcd",
        "hiberfil.sys",
        "pagefile.sys",
        "swapfile.sys",
        "winresume.exe",
        "winresume.efi",
        "winload.exe",
        "winload.efi"
    };

    /// <summary>
    /// Returns true when the path points to a Windows- or boot-critical file or directory tree.
    /// </summary>
    public static bool IsSystemCriticalPath(string? path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (IsCriticalFile(normalized))
        {
            return true;
        }

        foreach (var root in CriticalRoots.Value)
        {
            if (IsSameOrSubPath(normalized, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCriticalFile(string normalizedPath)
    {
        var fileName = Path.GetFileName(normalizedPath);
        if (fileName.Length == 0)
        {
            return false;
        }

        if (CriticalFiles.Contains(fileName))
        {
            return true;
        }

        // Guard BCD store wherever it lives (Boot\BCD typically) even if filename casing varies.
        return fileName.Equals("bcd", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildCriticalRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate, bool protectSubtree = true)
        {
            var normalized = Normalize(candidate);
            if (normalized.Length == 0)
            {
                return;
            }

            // Prefer the most protective stance if duplicates appear.
            if (roots.TryGetValue(normalized, out _))
            {
                return;
            }

            roots.Add(NormalizeAsDirectory(normalized, protectSubtree));
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            Add(windows);

            // Core OS subtrees.
            Add(Path.Combine(windows, "System32"));
            Add(Path.Combine(windows, "SysWOW64"));
            Add(Path.Combine(windows, "WinSxS"));
            Add(Path.Combine(windows, "SystemApps"));
            Add(Path.Combine(windows, "SystemResources"));
            Add(Path.Combine(windows, "servicing"));
            Add(Path.Combine(windows, "assembly"));
            Add(Path.Combine(windows, "Fonts"));
            Add(Path.Combine(windows, "INF"));
            Add(Path.Combine(windows, "System32", "drivers"));
            Add(Path.Combine(windows, "System32", "DriverStore"));
            Add(Path.Combine(windows, "System32", "catroot"));
            Add(Path.Combine(windows, "System32", "catroot2"));
        }

        // Boot and recovery.
        var systemDrive = !string.IsNullOrWhiteSpace(windows) ? Path.GetPathRoot(windows) : Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(systemDrive))
        {
            Add(Path.Combine(systemDrive, "Boot"));
            Add(Path.Combine(systemDrive, "EFI"));
            Add(Path.Combine(systemDrive, "Recovery"));
            Add(Path.Combine(systemDrive, "System Volume Information"));
        }

        // OS-owned areas under Program Files (avoid blocking third-party apps by being surgical).
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            Add(Path.Combine(programFiles, "WindowsApps"));
            Add(Path.Combine(programFiles, "Windows Defender"));
            Add(Path.Combine(programFiles, "Windows Security"));
            Add(Path.Combine(programFiles, "Common Files", "Microsoft Shared"));
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            Add(Path.Combine(programFilesX86, "WindowsApps"));
            Add(Path.Combine(programFilesX86, "Windows Defender"));
            Add(Path.Combine(programFilesX86, "Windows Security"));
            Add(Path.Combine(programFilesX86, "Common Files", "Microsoft Shared"));
        }

        // Core OS data under ProgramData.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            Add(Path.Combine(programData, "Microsoft", "Windows"));
            Add(Path.Combine(programData, "Microsoft", "Crypto"));
            Add(Path.Combine(programData, "Microsoft", "Protect"));
            Add(Path.Combine(programData, "Microsoft", "Windows Defender"));
        }

        return roots;
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return path.Trim().Trim('"');
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

        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
