using System;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Represents a single process or service within the Known Processes catalog.
/// </summary>
public sealed record ProcessCatalogEntry
{
    public ProcessCatalogEntry(
        string identifier,
        string displayName,
        string categoryKey,
        string categoryName,
        string? categoryDescription,
        ProcessRiskLevel riskLevel,
        ProcessActionPreference recommendedAction,
        string? rationale,
        bool isPattern,
        int categoryOrder,
        int entryOrder,
        string? serviceIdentifier = null)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must be provided.", nameof(identifier));
        }

        Identifier = NormalizeIdentifier(identifier);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? identifier.Trim() : displayName.Trim();
        CategoryKey = string.IsNullOrWhiteSpace(categoryKey) ? "general" : categoryKey.Trim();
        CategoryName = string.IsNullOrWhiteSpace(categoryName) ? CategoryKey : categoryName.Trim();
        CategoryDescription = string.IsNullOrWhiteSpace(categoryDescription) ? null : categoryDescription.Trim();
        RiskLevel = riskLevel;
        RecommendedAction = recommendedAction;
        Rationale = string.IsNullOrWhiteSpace(rationale) ? null : rationale.Trim();
        IsPattern = isPattern;
        CategoryOrder = categoryOrder;
        EntryOrder = entryOrder;
        ServiceIdentifier = isPattern ? null : NormalizeServiceIdentifier(string.IsNullOrWhiteSpace(serviceIdentifier) ? identifier : serviceIdentifier);
        if (!string.IsNullOrWhiteSpace(serviceIdentifier) && ServiceIdentifier is null)
        {
            throw new ArgumentException("Service identifier contains invalid characters.", nameof(serviceIdentifier));
        }
    }

    public string Identifier { get; init; }

    public string DisplayName { get; init; }

    public string CategoryKey { get; init; }

    public string CategoryName { get; init; }

    public string? CategoryDescription { get; init; }

    public ProcessRiskLevel RiskLevel { get; init; }

    public ProcessActionPreference RecommendedAction { get; init; }

    public string? Rationale { get; init; }

    public bool IsPattern { get; init; }

    public int CategoryOrder { get; init; }

    public int EntryOrder { get; init; }

    public string? ServiceIdentifier { get; init; }

    public bool SupportsServiceControl => !IsPattern && !string.IsNullOrWhiteSpace(ServiceIdentifier);

    public static string NormalizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }

    public static string? NormalizeServiceIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.IndexOfAny(new[] { '\\', '/', '*', '?', ':', ';', '|' }) >= 0)
        {
            return null;
        }

        if (trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }
}
