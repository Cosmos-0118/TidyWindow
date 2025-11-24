using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TidyWindow.Core.PathPilot;

namespace TidyWindow.App.ViewModels;

internal static class PathPilotVersionHeuristics
{
    private static readonly Regex GenericVersionPattern = new(@"(?<!\d)(?<version>\d+(?:\.\d+){1,2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonDottedPattern = new(@"python(?<major>\d)\.(?<minor>\d{1,2})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PythonCondensedPattern = new(@"python(?<digits>\d{2,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? InferInstallationVersion(PathPilotInstallation installation)
    {
        if (installation is null)
        {
            return null;
        }

        var inferred = InferFromPaths(installation.Directory, installation.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(inferred))
        {
            return inferred;
        }

        return InferFromExecutable(installation.ExecutablePath);
    }

    public static string? InferFromExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var fromPath = InferFromPaths(executablePath);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        return TryReadFileVersion(executablePath);
    }

    public static string? InferFromPaths(params string?[] candidates)
    {
        if (candidates is null || candidates.Length == 0)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            var version = ExtractPythonVersion(candidate) ?? ExtractGenericVersion(candidate);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return null;
    }

    private static string? TryReadFileVersion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            var candidates = new[]
            {
                info.ProductVersion,
                info.FileVersion,
                BuildVersionFromParts(info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart),
                BuildVersionFromParts(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
            };

            return candidates
                .Select(ExtractGenericVersion)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildVersionFromParts(int major, int minor, int build, int revision)
    {
        if (major <= 0)
        {
            return null;
        }

        var parts = new List<int> { major };
        if (minor >= 0)
        {
            parts.Add(minor);
        }

        if (build >= 0)
        {
            parts.Add(build);
        }

        if (revision >= 0)
        {
            parts.Add(revision);
        }

        while (parts.Count > 0 && parts[^1] <= 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.Count > 1 ? string.Join('.', parts) : null;
    }

    internal static string? ExtractPythonVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf("python", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var dotted = PythonDottedPattern.Match(text);
        if (dotted.Success)
        {
            return $"{dotted.Groups["major"].Value}.{dotted.Groups["minor"].Value}";
        }

        var condensed = PythonCondensedPattern.Match(text);
        if (condensed.Success)
        {
            var digits = condensed.Groups["digits"].Value;
            if (digits.Length == 2)
            {
                return $"{digits[0]}.{digits[1]}";
            }

            if (digits.Length == 3)
            {
                return $"{digits[0]}.{digits.Substring(1)}";
            }
        }

        return null;
    }

    internal static string? ExtractGenericVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = GenericVersionPattern.Match(text);
        return match.Success ? match.Groups["version"].Value : null;
    }
}
