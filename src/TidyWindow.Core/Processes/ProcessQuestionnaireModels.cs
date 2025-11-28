using System;
using System.Collections.Generic;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Declarative definition of the questionnaire shown during first-run.
/// </summary>
public sealed record ProcessQuestionnaireDefinition(IReadOnlyList<ProcessQuestion> Questions)
{
    public static ProcessQuestionnaireDefinition Empty { get; } = new(Array.Empty<ProcessQuestion>());
}

/// <summary>
/// Represents a single question in the questionnaire.
/// </summary>
public sealed record ProcessQuestion
{
    public ProcessQuestion(string id, string title, string prompt, IReadOnlyList<ProcessQuestionOption> options, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Question id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Question title is required.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Question prompt is required.", nameof(prompt));
        }

        if (options is null || options.Count == 0)
        {
            throw new ArgumentException("At least one option must be provided.", nameof(options));
        }

        Id = id.Trim().ToLowerInvariant();
        Title = title.Trim();
        Prompt = prompt.Trim();
        Options = options;
        Required = required;
    }

    public string Id { get; }

    public string Title { get; }

    public string Prompt { get; }

    public IReadOnlyList<ProcessQuestionOption> Options { get; }

    public bool Required { get; }
}

/// <summary>
/// Represents an option for a question (radio button, toggle, etc.).
/// </summary>
public sealed record ProcessQuestionOption
{
    public ProcessQuestionOption(string id, string label, string? description)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Option id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Option label is required.", nameof(label));
        }

        Id = id.Trim().ToLowerInvariant();
        Label = label.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public string Id { get; }

    public string Label { get; }

    public string? Description { get; }
}

/// <summary>
/// Result emitted by the questionnaire engine containing the processed answers and derived preferences.
/// </summary>
public sealed record ProcessQuestionnaireResult(
    ProcessQuestionnaireSnapshot Snapshot,
    IReadOnlyCollection<string> RecommendedProcessIds,
    IReadOnlyCollection<ProcessPreference> AppliedPreferences);
