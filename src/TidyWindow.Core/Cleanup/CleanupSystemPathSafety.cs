using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Centralized, conservative classification for system-critical paths used by cleanup flows.
/// Only blocks locations whose removal is likely to break Windows, drivers, or boot.
/// </summary>
public static class CleanupSystemPathSafety
{
    private static readonly Lazy<HashSet<string>> CriticalRoots = new(() => BuildCriticalRoots(), isThreadSafe: true);
    private static readonly ConcurrentDictionary<string, byte> AdditionalRoots = new(StringComparer.OrdinalIgnoreCase);
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

        if (IsCriticalFile(normalized))
        {
            return true;
        }

        // Explicitly allow Windows Temp as non-critical even though it lives under %WINDIR%.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            var windowsTemp = NormalizeAsDirectory(Path.Combine(windows, "Temp"), ensureTrailingSeparator: true);
            if (IsSameOrSubPath(normalized, windowsTemp))
            {
                return false;
            }
        }

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
