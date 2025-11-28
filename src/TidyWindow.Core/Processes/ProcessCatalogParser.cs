using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Parses the Known Processes catalog (JSON or legacy text) into structured entries.
/// </summary>
public sealed class ProcessCatalogParser
{
    private const string CatalogOverrideEnvironmentVariable = "TIDYWINDOW_PROCESS_CATALOG_PATH";
    private const string LegacyFileName = "listofknown.txt";
    private const string JsonFileName = "processes.catalog.json";

    private static readonly string[] CandidateFileNames = { JsonFileName, LegacyFileName };
    private static readonly Regex CategoryRegex = new("^(?<key>[A-Z0-9]+)\\.\\s+(?<label>.+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly char[] WhitespaceTerminatorCandidates = { ' ', '\t' };
    private static readonly char[] IdentifierTerminators = { ' ', '\t', '—', '-', '(' };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _catalogPath;

    public ProcessCatalogParser(string? catalogPath = null)
    {
        _catalogPath = string.IsNullOrWhiteSpace(catalogPath) ? ResolveCatalogPath() : catalogPath!;
    }

    public ProcessCatalogSnapshot LoadSnapshot()
    {
        if (_catalogPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return LoadSnapshotFromJson(_catalogPath);
        }

        return LoadSnapshotFromLegacyFile(_catalogPath);
    }

    private static ProcessCatalogSnapshot LoadSnapshotFromLegacyFile(string path)
    {
        var lines = File.ReadAllLines(path);
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

        return new ProcessCatalogSnapshot(path, DateTimeOffset.UtcNow, orderedCategories, orderedEntries);
    }

    private static ProcessCatalogSnapshot LoadSnapshotFromJson(string path)
    {
        ProcessCatalogJsonDocument? document;
        try
        {
            var json = File.ReadAllText(path);
            document = JsonSerializer.Deserialize<ProcessCatalogJsonDocument>(json, SerializerOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidDataException($"Failed to parse '{path}'.", exception);
        }

        if (document is null)
        {
            throw new InvalidDataException($"'{path}' does not contain a valid catalog payload.");
        }

        var categories = new Dictionary<string, ProcessCatalogCategory>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ProcessCatalogEntry>();
        var seenIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categoryOrderCursor = 0;

        if (document.Categories is not null)
        {
            foreach (var model in document.Categories)
            {
                if (model is null || string.IsNullOrWhiteSpace(model.Key))
                {
                    continue;
                }

                var key = model.Key.Trim();
                var name = string.IsNullOrWhiteSpace(model.Name) ? key : model.Name.Trim();
                var description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
                var order = ResolveOrder(model.Order, ref categoryOrderCursor);
                var category = new ProcessCatalogCategory(key, name, description, model.IsCaution ?? false, order);
                categories.TryAdd(key, category);
            }
        }

        if (!categories.ContainsKey("general"))
        {
            var fallback = new ProcessCatalogCategory("general", "General", null, false, ResolveOrder(null, ref categoryOrderCursor));
            categories["general"] = fallback;
        }

        var entryOrderCursor = 0;
        if (document.Entries is not null)
        {
            foreach (var model in document.Entries)
            {
                if (model is null || string.IsNullOrWhiteSpace(model.Identifier))
                {
                    continue;
                }

                var normalized = ProcessCatalogEntry.NormalizeIdentifier(model.Identifier);
                if (string.IsNullOrWhiteSpace(normalized) || !seenIdentifiers.Add(normalized))
                {
                    continue;
                }

                var categoryKey = string.IsNullOrWhiteSpace(model.CategoryKey) ? "general" : model.CategoryKey.Trim();
                if (!categories.TryGetValue(categoryKey, out var category))
                {
                    var derivedName = string.IsNullOrWhiteSpace(model.CategoryName) ? categoryKey : model.CategoryName.Trim();
                    var derivedDescription = string.IsNullOrWhiteSpace(model.CategoryDescription) ? null : model.CategoryDescription.Trim();
                    var derivedOrder = ResolveOrder(null, ref categoryOrderCursor);
                    var isCaution = string.Equals(categoryKey, "caution", StringComparison.OrdinalIgnoreCase);
                    category = new ProcessCatalogCategory(categoryKey, derivedName, derivedDescription, isCaution, derivedOrder);
                    categories[categoryKey] = category;
                }

                var risk = ResolveRisk(model.Risk, category.IsCaution);
                var action = ResolveAction(model.RecommendedAction, category.IsCaution);
                var rationale = string.IsNullOrWhiteSpace(model.Rationale) ? null : model.Rationale.Trim();
                var isPattern = model.IsPattern ?? IsPattern(model.DisplayName ?? model.Identifier);
                var displayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Identifier.Trim() : model.DisplayName.Trim();
                var entryOrder = ResolveOrder(model.Order, ref entryOrderCursor);

                var entry = new ProcessCatalogEntry(
                    normalized,
                    displayName,
                    category.Key,
                    category.Name,
                    category.Description,
                    risk,
                    action,
                    rationale,
                    isPattern,
                    category.Order,
                    entryOrder);

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

        return new ProcessCatalogSnapshot(path, DateTimeOffset.UtcNow, orderedCategories, orderedEntries);
    }

    private static IReadOnlyList<string> ExtractProcessTokens(string line)
    {
        var sanitized = RemoveAnnotations(line);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return Array.Empty<string>();
        }

        if (sanitized.IndexOfAny(WhitespaceTerminatorCandidates) >= 0 &&
            !sanitized.Contains('/') &&
            !sanitized.Contains('\\'))
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
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length > 1)
        {
            trimmed = trimmed[1..^1];
        }

        var allowWhitespace = trimmed.Contains('\\');
        if (!allowWhitespace)
        {
            var terminatorIndex = trimmed.IndexOfAny(IdentifierTerminators);
            if (terminatorIndex >= 0)
            {
                trimmed = trimmed[..terminatorIndex];
            }
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

    private sealed record ProcessCatalogJsonDocument
    {
        public List<ProcessCatalogJsonCategory>? Categories { get; init; }

        public List<ProcessCatalogJsonEntry>? Entries { get; init; }
    }

    private sealed record ProcessCatalogJsonCategory
    {
        public string? Key { get; init; }

        public string? Name { get; init; }

        public string? Description { get; init; }

        public bool? IsCaution { get; init; }

        public int? Order { get; init; }
    }

    private sealed record ProcessCatalogJsonEntry
    {
        public string? Identifier { get; init; }

        public string? DisplayName { get; init; }

        public string? CategoryKey { get; init; }

        public string? CategoryName { get; init; }

        public string? CategoryDescription { get; init; }

        public string? Risk { get; init; }

        public string? RecommendedAction { get; init; }

        public string? Rationale { get; init; }

        public bool? IsPattern { get; init; }

        public int? Order { get; init; }
    }

    private static ProcessRiskLevel ResolveRisk(string? value, bool isCaution)
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse<ProcessRiskLevel>(value, true, out var parsed))
        {
            return parsed;
        }

        return isCaution ? ProcessRiskLevel.Caution : ProcessRiskLevel.Safe;
    }

    private static ProcessActionPreference ResolveAction(string? value, bool isCaution)
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse<ProcessActionPreference>(value, true, out var parsed))
        {
            return parsed;
        }

        return isCaution ? ProcessActionPreference.Keep : ProcessActionPreference.AutoStop;
    }

    private static int ResolveOrder(int? declaredOrder, ref int cursor)
    {
        if (declaredOrder.HasValue && declaredOrder.Value > 0)
        {
            cursor = Math.Max(cursor, declaredOrder.Value);
            return declaredOrder.Value;
        }

        cursor++;
        return cursor;
    }

    private static string ResolveCatalogPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(CatalogOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidatePaths = new List<string>();

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            AppendCandidatePaths(candidatePaths, directory.FullName);
            directory = directory.Parent;
        }

        foreach (var candidate in candidatePaths)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Unable to locate '{JsonFileName}' or '{LegacyFileName}'. Set {CatalogOverrideEnvironmentVariable} to override the path.");
    }

    private static void AppendCandidatePaths(List<string> target, string root)
    {
        foreach (var fileName in CandidateFileNames)
        {
            target.Add(Path.Combine(root, fileName));
            target.Add(Path.Combine(root, "catalog", fileName));
            target.Add(Path.Combine(root, "data", "catalog", fileName));
        }
    }
}
