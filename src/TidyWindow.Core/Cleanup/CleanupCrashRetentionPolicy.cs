using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyWindow.Core.Cleanup;

internal static class CleanupCrashRetentionPolicy
{
    public static IReadOnlySet<string> GetPathsToProtect(IEnumerable<CleanupFileContext> contexts)
    {
        if (contexts is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var snapshot = CleanupSignatureCatalog.Snapshot;
        var retentionCount = Math.Max(snapshot.CrashDumpNewestRetentionCount, 0);
        if (retentionCount == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var groups = new Dictionary<string, List<CleanupFileContext>>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in contexts)
        {
            if (!CleanupIntelligence.IsCrashArtifact(context))
            {
                continue;
            }

            var key = CleanupIntelligence.GetCrashProductKey(context);
            if (string.IsNullOrWhiteSpace(key))
            {
                key = "__fallback";
            }

            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<CleanupFileContext>();
                groups[key] = bucket;
            }

            bucket.Add(context);
        }

        if (groups.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in groups)
        {
            var ordered = pair.Value
                .OrderByDescending(static ctx => CleanupIntelligence.GetMostRecentTimestamp(ctx))
                .Take(retentionCount);

            foreach (var context in ordered)
            {
                if (!string.IsNullOrWhiteSpace(context.FullPath))
                {
                    protectedPaths.Add(context.FullPath);
                }
            }
        }

        return protectedPaths;
    }
}
