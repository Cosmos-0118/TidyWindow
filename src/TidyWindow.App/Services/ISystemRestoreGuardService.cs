using System;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

/// <summary>
/// Coordinates safety checks that ensure high-impact operations have a fresh System Restore checkpoint.
/// </summary>
public interface ISystemRestoreGuardService
{
    /// <summary>
    /// Evaluates whether the latest System Restore checkpoint meets the supplied freshness threshold.
    /// </summary>
    /// <param name="freshnessThreshold">Maximum permitted age for the checkpoint.</param>
    /// <param name="cancellationToken">Token used to cancel the evaluation.</param>
    Task<SystemRestoreGuardCheckResult> CheckAsync(TimeSpan freshnessThreshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a prompt so other surfaces (for example Essentials) can guide the user through creating a restore point.
    /// </summary>
    /// <param name="prompt">Prompt metadata to propagate.</param>
    void RequestPrompt(SystemRestoreGuardPrompt prompt);

    /// <summary>
    /// Attempts to retrieve any pending prompt that has not yet been shown.
    /// </summary>
    bool TryConsumePendingPrompt(out SystemRestoreGuardPrompt prompt);

    /// <summary>
    /// Raised when a new prompt should be shown immediately.
    /// </summary>
    event EventHandler<SystemRestoreGuardPromptEventArgs>? PromptRequested;
}
