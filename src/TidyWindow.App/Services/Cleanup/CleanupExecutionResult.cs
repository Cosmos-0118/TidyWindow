using System.Collections.Generic;

namespace TidyWindow.App.Services.Cleanup;

public sealed class CleanupExecutionResult
{
    public CleanupExecutionResult(int processed, int succeeded, IReadOnlyList<string> messages, IReadOnlyList<string> errors)
    {
        Processed = processed;
        Succeeded = succeeded;
        Messages = messages;
        Errors = errors;
    }

    public int Processed { get; }

    public int Succeeded { get; }

    public IReadOnlyList<string> Messages { get; }

    public IReadOnlyList<string> Errors { get; }
}
