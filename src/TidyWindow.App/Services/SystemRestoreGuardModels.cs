using System;

namespace TidyWindow.App.Services;

/// <summary>
/// Represents the outcome of a System Restore freshness check.
/// </summary>
/// <param name="IsSatisfied">True when a checkpoint exists within the supplied threshold.</param>
/// <param name="LatestRestorePointUtc">Creation timestamp of the newest checkpoint in UTC.</param>
/// <param name="ErrorMessage">Optional error description when discovery fails.</param>
public sealed record SystemRestoreGuardCheckResult(bool IsSatisfied, DateTimeOffset? LatestRestorePointUtc, string? ErrorMessage);

/// <summary>
/// Carries the context required to show a restore guard prompt.
/// </summary>
public sealed class SystemRestoreGuardPrompt
{
    public SystemRestoreGuardPrompt(
        string source,
        string headline,
        string body,
        string primaryActionLabel = "Open restore manager",
        string secondaryActionLabel = "Later")
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Source must be provided.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(headline))
        {
            throw new ArgumentException("Headline must be provided.", nameof(headline));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Body must be provided.", nameof(body));
        }

        Source = source;
        Headline = headline;
        Body = body;
        PrimaryActionLabel = string.IsNullOrWhiteSpace(primaryActionLabel) ? "Open restore manager" : primaryActionLabel;
        SecondaryActionLabel = string.IsNullOrWhiteSpace(secondaryActionLabel) ? "Later" : secondaryActionLabel;
        RequestedAt = DateTimeOffset.UtcNow;
    }

    public string Source { get; }

    public string Headline { get; }

    public string Body { get; }

    public string PrimaryActionLabel { get; }

    public string SecondaryActionLabel { get; }

    public DateTimeOffset RequestedAt { get; }
}

/// <summary>
/// Event arguments raised when a new restore guard prompt becomes available.
/// </summary>
public sealed class SystemRestoreGuardPromptEventArgs : EventArgs
{
    public SystemRestoreGuardPromptEventArgs(SystemRestoreGuardPrompt prompt)
    {
        Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    public SystemRestoreGuardPrompt Prompt { get; }
}
