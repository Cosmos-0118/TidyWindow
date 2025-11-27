using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Uninstall;

namespace TidyWindow.App.Services.Cleanup;

public sealed class AppCleanupPlanner
{
    private static readonly string[] ShortcutRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    };

    private static readonly string[] BenignExtensions = { ".log", ".txt", ".ini", ".cfg", ".md" };

    public Task<AppCleanupPlan> BuildPlanAsync(InstalledApp app, CancellationToken cancellationToken)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        var suggestions = new List<CleanupSuggestion>();
        var deferred = new List<string>();

        var installLocation = NormalizePath(app.InstallLocation);
        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
        {
            var folderSuggestion = TryCreateFolderSuggestion(installLocation);
            if (folderSuggestion is not null)
            {
                suggestions.Add(folderSuggestion);
            }

            suggestions.AddRange(FindShortcutSuggestions(installLocation, app.Name, cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(app.RegistryKey))
        {
            deferred.Add(app.RegistryKey!);
        }

        return Task.FromResult(new AppCleanupPlan(app, suggestions, deferred));
    }

    public Task<CleanupExecutionResult> ApplyAsync(IEnumerable<CleanupSuggestion> selections, CancellationToken cancellationToken)
    {
        if (selections is null)
        {
            throw new ArgumentNullException(nameof(selections));
        }

        var processed = 0;
        var succeeded = 0;
        var messages = new List<string>();
        var errors = new List<string>();

        foreach (var suggestion in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            try
            {
                switch (suggestion.Kind)
                {
                    case CleanupSuggestionKind.DeleteFolder:
                        if (DeleteFolder(suggestion.Path))
                        {
                            succeeded++;
                            messages.Add($"Deleted {suggestion.Path}");
                        }
                        else
                        {
                            errors.Add($"Skipped {suggestion.Path} because it is not empty or missing.");
                        }
                        break;
                    case CleanupSuggestionKind.DeleteShortcut:
                        if (DeleteFile(suggestion.Path))
                        {
                            succeeded++;
                            messages.Add($"Removed shortcut {suggestion.Path}");
                        }
                        else
                        {
                            errors.Add($"Shortcut not found: {suggestion.Path}");
                        }
                        break;
                    default:
                        errors.Add($"Unsupported cleanup action for {suggestion.Path}");
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{suggestion.Path}: {ex.Message}");
            }
        }

        return Task.FromResult(new CleanupExecutionResult(processed, succeeded, messages, errors));
    }

    private static CleanupSuggestion? TryCreateFolderSuggestion(string installLocation)
    {
        try
        {
            if (!Directory.Exists(installLocation))
            {
                return null;
            }

            var entries = Directory.EnumerateFileSystemEntries(installLocation).Take(12).ToList();
            if (entries.Count == 0)
            {
                return new CleanupSuggestion(
                    id: $"folder::{installLocation.ToLowerInvariant()}",
                    kind: CleanupSuggestionKind.DeleteFolder,
                    path: installLocation,
                    title: "Remove empty install folder",
                    description: "Folder is empty after uninstall.",
                    isSafe: true);
            }

            if (entries.All(entry => IsBenignFile(entry)))
            {
                return new CleanupSuggestion(
                    id: $"folder::{installLocation.ToLowerInvariant()}",
                    kind: CleanupSuggestionKind.DeleteFolder,
                    path: installLocation,
                    title: "Remove leftover install folder",
                    description: "Contains only log/readme files.",
                    isSafe: true);
            }
        }
        catch
        {
            // Ignore IO issues and skip suggestion.
        }

        return null;
    }

    private static IEnumerable<CleanupSuggestion> FindShortcutSuggestions(string installLocation, string appName, CancellationToken cancellationToken)
    {
        var found = 0;
        var normalizedInstall = installLocation.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        foreach (var root in ShortcutRoots
                 .Select(NormalizePath)
                 .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path!)))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root!, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var shortcutPath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (found >= 32)
                {
                    yield break;
                }

                if (!ShortcutResolver.TryGetTarget(shortcutPath, out var target))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                var normalizedTarget = target.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
                if (!normalizedTarget.StartsWith(normalizedInstall, StringComparison.Ordinal))
                {
                    continue;
                }

                found++;
                yield return new CleanupSuggestion(
                    id: $"shortcut::{shortcutPath.ToLowerInvariant()}",
                    kind: CleanupSuggestionKind.DeleteShortcut,
                    path: shortcutPath,
                    title: "Remove shortcut",
                    description: $"Shortcut still points to {appName} files.",
                    isSafe: true);
            }
        }
    }

    private static bool DeleteFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            return false;
        }

        Directory.Delete(path, recursive: false);
        return true;
    }

    private static bool DeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static bool IsBenignFile(string entry)
    {
        try
        {
            if (Directory.Exists(entry))
            {
                return false;
            }

            var extension = Path.GetExtension(entry);
            return BenignExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return path;
        }
    }
}
