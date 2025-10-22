using System;
using System.Collections.Immutable;
using System.IO;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Describes a high-impact maintenance automation script surfaced in the Essentials hub.
/// </summary>
public sealed record EssentialsTaskDefinition(
    string Id,
    string Name,
    string Category,
    string Summary,
    ImmutableArray<string> Highlights,
    string RelativeScriptPath,
    string? DocumentationLink = null)
{
    public string ResolveScriptPath()
    {
        if (string.IsNullOrWhiteSpace(RelativeScriptPath))
        {
            throw new InvalidOperationException("Task script path cannot be empty.");
        }

        if (Path.IsPathRooted(RelativeScriptPath))
        {
            return RelativeScriptPath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, RelativeScriptPath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, RelativeScriptPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{RelativeScriptPath}'.", RelativeScriptPath);
    }
}
