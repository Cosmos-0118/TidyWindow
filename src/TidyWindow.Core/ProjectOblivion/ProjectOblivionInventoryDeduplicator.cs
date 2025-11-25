using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TidyWindow.Core.ProjectOblivion;

public enum ProjectOblivionMergeStrategy
{
    None,
    ByIdentity
}

public static class ProjectOblivionInventoryDeduplicator
{
    private static readonly Dictionary<string, int> SourcePriority = new(StringComparer.OrdinalIgnoreCase)
    {
        ["registry"] = 0,
        ["appx"] = 1,
        ["winget"] = 2,
        ["store"] = 3,
        ["steam"] = 4,
        ["epic"] = 5,
        ["portable"] = 6,
        ["shortcut"] = 7
    };

    public static ImmutableArray<ProjectOblivionApp> Merge(IEnumerable<ProjectOblivionApp>? apps, ProjectOblivionMergeStrategy strategy)
    {
        if (apps is null)
        {
            return ImmutableArray<ProjectOblivionApp>.Empty;
        }

        if (strategy == ProjectOblivionMergeStrategy.None)
        {
            return apps.ToImmutableArray();
        }

        var comparer = new AppIdentityComparer();
        return apps
            .GroupBy(BuildAppIdentity, comparer)
            .Select(MergeGroup)
            .ToImmutableArray();
    }

    private static ProjectOblivionApp MergeGroup(IGrouping<AppIdentity, ProjectOblivionApp> group)
    {
        var ordered = group
            .OrderBy(GetSourceScore)
            .ThenByDescending(app => app.EstimatedSizeBytes ?? 0)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primary = ordered[0];
        var installRoot = SelectFirstNonEmpty(ordered.Select(a => ProjectOblivionPathHelper.NormalizeDirectoryCandidate(a.InstallRoot)));
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            installRoot = SelectFirstNonEmpty(ordered
                .SelectMany(EnumerateAllInstallRoots)
                .Select(ProjectOblivionPathHelper.NormalizeDirectoryCandidate));
        }

        var installRoots = ordered
            .SelectMany(a => EnumerateStrings(a.InstallRoots))
            .Select(ProjectOblivionPathHelper.NormalizeDirectoryCandidate)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        if (string.IsNullOrWhiteSpace(installRoot) && installRoots.Length > 0)
        {
            installRoot = installRoots[0];
        }

        var tags = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.Tags)));
        var artifactHints = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.ArtifactHints)));
        var processHints = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.ProcessHints)));
        var serviceHints = BuildDistinctStrings(ordered.SelectMany(a => EnumerateStrings(a.ServiceHints)));
        var managerHints = ordered
            .SelectMany(a => EnumerateManagerHints(a.ManagerHints))
            .Distinct()
            .ToImmutableArray();

        var estimatedSize = primary.EstimatedSizeBytes;
        if (estimatedSize is null or <= 0)
        {
            estimatedSize = ordered
                .Select(a => a.EstimatedSizeBytes)
                .FirstOrDefault(value => value.HasValue && value.Value > 0);
        }

        var scope = string.IsNullOrWhiteSpace(primary.Scope)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Scope))
            : primary.Scope;
        var publisher = string.IsNullOrWhiteSpace(primary.Publisher)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Publisher))
            : primary.Publisher;
        var version = string.IsNullOrWhiteSpace(primary.Version)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Version))
            : primary.Version;
        var source = string.IsNullOrWhiteSpace(primary.Source)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Source))
            : primary.Source;
        var confidence = string.IsNullOrWhiteSpace(primary.Confidence)
            ? SelectFirstNonEmpty(ordered.Select(a => a.Confidence))
            : primary.Confidence;
        var registry = primary.Registry ?? ordered.Select(a => a.Registry).FirstOrDefault(r => r is not null);

        var normalizedInstallRoots = installRoots.Length > 0
            ? installRoots
            : (primary.InstallRoots.IsDefault ? ImmutableArray<string>.Empty : primary.InstallRoots);

        var normalizedTags = tags.Length > 0
            ? tags
            : (primary.Tags.IsDefault ? ImmutableArray<string>.Empty : primary.Tags);

        var normalizedArtifacts = artifactHints.Length > 0
            ? artifactHints
            : (primary.ArtifactHints.IsDefault ? ImmutableArray<string>.Empty : primary.ArtifactHints);

        var normalizedProcessHints = processHints.Length > 0
            ? processHints
            : (primary.ProcessHints.IsDefault ? ImmutableArray<string>.Empty : primary.ProcessHints);

        var normalizedServiceHints = serviceHints.Length > 0
            ? serviceHints
            : (primary.ServiceHints.IsDefault ? ImmutableArray<string>.Empty : primary.ServiceHints);

        var normalizedManagerHints = managerHints.Length > 0
            ? managerHints
            : (primary.ManagerHints.IsDefault ? ImmutableArray<ProjectOblivionManagerHint>.Empty : primary.ManagerHints);

        return primary with
        {
            InstallRoot = installRoot ?? primary.InstallRoot,
            InstallRoots = normalizedInstallRoots,
            Tags = normalizedTags,
            ArtifactHints = normalizedArtifacts,
            ProcessHints = normalizedProcessHints,
            ServiceHints = normalizedServiceHints,
            ManagerHints = normalizedManagerHints,
            EstimatedSizeBytes = estimatedSize ?? primary.EstimatedSizeBytes,
            Scope = scope ?? primary.Scope,
            Publisher = publisher ?? primary.Publisher,
            Version = version ?? primary.Version,
            Source = source ?? primary.Source,
            Confidence = confidence ?? primary.Confidence,
            Registry = registry ?? primary.Registry
        };
    }

    private static int GetSourceScore(ProjectOblivionApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.Source) && SourcePriority.TryGetValue(app.Source, out var score))
        {
            return score;
        }

        return SourcePriority.Count + 1;
    }

    private static AppIdentity BuildAppIdentity(ProjectOblivionApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.PackageFamilyName))
        {
            return AppIdentity.PackageFamily(app.PackageFamilyName.Trim().ToLowerInvariant());
        }

        var managerKey = BuildManagerKey(app);
        if (!string.IsNullOrWhiteSpace(managerKey))
        {
            return AppIdentity.Manager(managerKey);
        }

        var installKey = BuildInstallRootKey(app);
        if (!string.IsNullOrWhiteSpace(installKey))
        {
            return AppIdentity.InstallRoot(installKey);
        }

        var normalizedName = NormalizeNameForKey(app.Name, app.AppId);
        var normalizedPublisher = NormalizeNameForKey(app.Publisher, string.Empty);
        return AppIdentity.Name(normalizedName, normalizedPublisher);
    }

    private static string? BuildManagerKey(ProjectOblivionApp app)
    {
        if (app.ManagerHints.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var hint in app.ManagerHints)
        {
            if (string.IsNullOrWhiteSpace(hint.Manager) || string.IsNullOrWhiteSpace(hint.PackageId))
            {
                continue;
            }

            return $"manager:{hint.Manager.Trim().ToLowerInvariant()}|pkg:{hint.PackageId.Trim().ToLowerInvariant()}";
        }

        return null;
    }

    private static string? BuildInstallRootKey(ProjectOblivionApp app)
    {
        foreach (var raw in EnumerateAllInstallRoots(app))
        {
            var normalized = ProjectOblivionPathHelper.NormalizeDirectoryCandidate(raw);
            if (!string.IsNullOrWhiteSpace(normalized) && ProjectOblivionPathHelper.IsHighConfidenceInstallPath(normalized))
            {
                return $"install:{normalized.ToLowerInvariant()}";
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateAllInstallRoots(ProjectOblivionApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.InstallRoot))
        {
            yield return app.InstallRoot;
        }

        if (!app.InstallRoots.IsDefaultOrEmpty)
        {
            foreach (var root in app.InstallRoots)
            {
                yield return root;
            }
        }

        if (!app.ArtifactHints.IsDefaultOrEmpty)
        {
            foreach (var hint in app.ArtifactHints)
            {
                yield return hint;
            }
        }

        if (!string.IsNullOrWhiteSpace(app.Registry?.InstallLocation))
        {
            yield return app.Registry!.InstallLocation;
        }
    }

    private static ImmutableArray<string> BuildDistinctStrings(IEnumerable<string> values)
    {
        return values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static IEnumerable<string> EnumerateStrings(ImmutableArray<string> values)
    {
        return values.IsDefaultOrEmpty ? Array.Empty<string>() : values;
    }

    private static IEnumerable<ProjectOblivionManagerHint> EnumerateManagerHints(ImmutableArray<ProjectOblivionManagerHint> values)
    {
        return values.IsDefaultOrEmpty ? Array.Empty<ProjectOblivionManagerHint>() : values;
    }

    private static string? SelectFirstNonEmpty(IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string NormalizeNameForKey(string? value, string fallback)
    {
        var input = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = VersionSuffixPattern.Replace(input.Trim(), string.Empty);
        trimmed = trimmed.Replace("®", string.Empty, StringComparison.Ordinal)
            .Replace("™", string.Empty, StringComparison.Ordinal);
        var normalized = NonAlphaNumericPattern.Replace(trimmed.ToLowerInvariant(), " ").Trim();
        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex VersionSuffixPattern = new("\\s+(?:v)?\\d+(?:[\\._-]\\d+)*(?:\\s*(?:x64|x86))?\\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NonAlphaNumericPattern = new("[^a-z0-9]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly record struct AppIdentity(string Kind, string Primary, string? Secondary)
    {
        public static AppIdentity PackageFamily(string value) => new("family", value ?? string.Empty, string.Empty);
        public static AppIdentity Manager(string value) => new("manager", value ?? string.Empty, string.Empty);
        public static AppIdentity InstallRoot(string value) => new("install", value ?? string.Empty, string.Empty);
        public static AppIdentity Name(string value, string? publisher) => new("name", value ?? string.Empty, publisher ?? string.Empty);
    }

    private sealed class AppIdentityComparer : IEqualityComparer<AppIdentity>
    {
        public bool Equals(AppIdentity x, AppIdentity y)
        {
            if (!string.Equals(x.Kind, y.Kind, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(x.Primary, y.Primary, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(x.Kind, "name", StringComparison.Ordinal))
            {
                return string.Equals(x.Secondary, y.Secondary, StringComparison.Ordinal);
            }

            if (string.IsNullOrEmpty(x.Secondary) || string.IsNullOrEmpty(y.Secondary))
            {
                return true;
            }

            return string.Equals(x.Secondary, y.Secondary, StringComparison.Ordinal);
        }

        public int GetHashCode(AppIdentity obj)
        {
            var hash = HashCode.Combine(obj.Kind, obj.Primary);
            if (!string.Equals(obj.Kind, "name", StringComparison.Ordinal) && !string.IsNullOrEmpty(obj.Secondary))
            {
                hash = HashCode.Combine(hash, obj.Secondary);
            }

            return hash;
        }
    }
}
