using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Parses <c>listofknown.txt</c> into structured catalog entries.
/// </summary>
public sealed class ProcessCatalogParser
{
    private const string CatalogOverrideEnvironmentVariable = "TIDYWINDOW_PROCESS_CATALOG_PATH";
    private const string DefaultFileName = "listofknown.txt";

    private static readonly Regex CategoryRegex = new("^(?<key>[A-Z0-9]+)\\.\\s+(?<label>.+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _catalogPath;

    public ProcessCatalogParser(string? catalogPath = null)
    {
        _catalogPath = string.IsNullOrWhiteSpace(catalogPath) ? ResolveCatalogPath() : catalogPath!;
    }

    public ProcessCatalogSnapshot LoadSnapshot()
    {
        var lines = File.ReadAllLines(_catalogPath);
        var entries = new List<ProcessCatalogEntry>();
        var categories = new Dictionary<string, ProcessCatalogCategory>(StringComparer.OrdinalIgnoreCase);
        var seenIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string currentCategoryKey = "general";
        string currentCategoryName = "General";
        string? currentCategoryDescription = null;
        bool cautionSection = false;
        int categoryOrder = 0;
        int entryOrder = 0;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("🔁", StringComparison.Ordinal) ||
                trimmed.StartsWith("🧾", StringComparison.Ordinal) ||
                trimmed.StartsWith("1)", StringComparison.Ordinal))
            {
                break;
            }

            var categoryMatch = CategoryRegex.Match(trimmed);
            if (categoryMatch.Success)
            {
                cautionSection = false;
                categoryOrder++;
                currentCategoryKey = categoryMatch.Groups["key"].Value.Trim();
                var label = categoryMatch.Groups["label"].Value.Trim();
                currentCategoryName = ExtractCategoryName(label, out currentCategoryDescription);

                categories[currentCategoryKey] = new ProcessCatalogCategory(
                    currentCategoryKey,
                    currentCategoryName,
                    currentCategoryDescription,
                    false,
                    categoryOrder);
                continue;
            }

            if (trimmed.StartsWith("⚠️", StringComparison.Ordinal))
            {
                cautionSection = true;
                if (!categories.ContainsKey("caution"))
                {
                    categoryOrder++;
                    categories["caution"] = new ProcessCatalogCategory(
                        "caution",
                        "Caution",
                        "Review before stopping",
                        true,
                        categoryOrder);
                }

                currentCategoryKey = "caution";
                currentCategoryName = "Caution";
                currentCategoryDescription = "Review before stopping";
                continue;
            }

            if (trimmed.StartsWith("✅", StringComparison.Ordinal) ||
                trimmed.StartsWith("Grouped", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("These are", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Run as admin", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = ExtractProcessTokens(trimmed);
            if (tokens.Count == 0)
            {
                continue;
            }

            var rationale = ExtractAnnotation(rawLine);
            foreach (var token in tokens)
            {
                var normalized = ProcessCatalogEntry.NormalizeIdentifier(token);
                if (!seenIdentifiers.Add(normalized))
                {
                    continue;
                }

                var entry = new ProcessCatalogEntry(
                    normalized,
                    token,
                    currentCategoryKey,
                    currentCategoryName,
                    currentCategoryDescription,
                    cautionSection ? ProcessRiskLevel.Caution : ProcessRiskLevel.Safe,
                    cautionSection ? ProcessActionPreference.Keep : ProcessActionPreference.AutoStop,
                    rationale,
                    IsPattern(token),
                    categoryOrder,
                    ++entryOrder);

                entries.Add(entry);
            }
        }

        var orderedCategories = categories.Values
            .OrderBy(static category => category.Order)
            .ToImmutableArray();

        var orderedEntries = entries
            .OrderBy(static entry => entry.CategoryOrder)
            .ThenBy(static entry => entry.EntryOrder)
            .ToImmutableArray();

        return new ProcessCatalogSnapshot(_catalogPath, DateTimeOffset.UtcNow, orderedCategories, orderedEntries);
    }

    private static IReadOnlyList<string> ExtractProcessTokens(string line)
    {
        var sanitized = RemoveAnnotations(line);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return Array.Empty<string>();
        }

        if (sanitized.IndexOfAny(new[] { ' ', '\t' }) >= 0 && !sanitized.Contains('/'))
        {
            return Array.Empty<string>();
        }

        var segments = sanitized.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var token = ExtractIdentifier(segment);
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        return tokens.Count == 0 ? Array.Empty<string>() : tokens;
    }

    private static string? ExtractAnnotation(string rawLine)
    {
        var annotations = new List<string>();

        var hashIndex = rawLine.IndexOf('#');
        if (hashIndex >= 0)
        {
            annotations.Add(rawLine[(hashIndex + 1)..].Trim());
            rawLine = rawLine[..hashIndex];
        }

        var dashIndex = rawLine.IndexOf('—');
        if (dashIndex >= 0)
        {
            annotations.Add(rawLine[(dashIndex + 1)..].Trim());
            rawLine = rawLine[..dashIndex];
        }

        var parenIndex = rawLine.IndexOf('(');
        if (parenIndex >= 0)
        {
            var closing = rawLine.IndexOf(')', parenIndex + 1);
            string? inside = closing > parenIndex
                ? rawLine[(parenIndex + 1)..closing]
                : rawLine[(parenIndex + 1)..];

            if (!string.IsNullOrWhiteSpace(inside))
            {
                annotations.Add(inside.Trim());
            }
        }

        var annotation = string.Join(' ', annotations.Where(static text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(annotation) ? null : annotation;
    }

    private static string RemoveAnnotations(string line)
    {
        var candidate = line;

        var hashIndex = candidate.IndexOf('#');
        if (hashIndex >= 0)
        {
            candidate = candidate[..hashIndex];
        }

        var dashIndex = candidate.IndexOf('—');
        if (dashIndex >= 0)
        {
            candidate = candidate[..dashIndex];
        }

        dashIndex = candidate.IndexOf('(');
        if (dashIndex >= 0)
        {
            candidate = candidate[..dashIndex];
        }

        return candidate.Trim();
    }

    private static string? ExtractIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var terminatorIndex = trimmed.IndexOfAny(new[] { ' ', '\t', '—', '-', '(' });
        if (terminatorIndex >= 0)
        {
            trimmed = trimmed[..terminatorIndex];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.Trim(',', ';', '.');
    }

    private static bool IsPattern(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return candidate.IndexOf('*') >= 0 || candidate.IndexOf('?') >= 0 || candidate.IndexOf('_') >= 0;
    }

    private static string ExtractCategoryName(string label, out string? description)
    {
        description = null;
        if (string.IsNullOrWhiteSpace(label))
        {
            return "General";
        }

        var trimmed = label.Trim();
        var parenIndex = trimmed.IndexOf('(');
        if (parenIndex >= 0)
        {
            var closing = trimmed.IndexOf(')', parenIndex + 1);
            if (closing > parenIndex)
            {
                description = trimmed[(parenIndex + 1)..closing].Trim();
                trimmed = trimmed[..parenIndex].Trim();
            }
            else
            {
                description = trimmed[(parenIndex + 1)..].Trim();
                trimmed = trimmed[..parenIndex].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "General" : trimmed;
    }

    private static string ResolveCatalogPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(CatalogOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidatePaths = new List<string>
        {
            Path.Combine(baseDirectory, "catalog", DefaultFileName),
            Path.Combine(baseDirectory, DefaultFileName)
        };

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidatePaths.Add(Path.Combine(directory.FullName, DefaultFileName));
            candidatePaths.Add(Path.Combine(directory.FullName, "data", "catalog", DefaultFileName));
            directory = directory.Parent;
        }

        foreach (var candidate in candidatePaths)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Unable to locate '{DefaultFileName}'. Set {CatalogOverrideEnvironmentVariable} to override the path.");
    }
}
