using System.IO.Enumeration;

namespace TidyWindow.Next.Core;

/// <summary>Decides which files in a folder belong to a rule. Scanning and cleaning share it, so the size shown is the size removed.</summary>
public readonly struct CleanupFilter
{
    private readonly string[] masks;
    private readonly long cutoffTicks;

    public CleanupFilter(CleanupRule rule, DateTime nowUtc)
    {
        masks = rule.Masks;
        cutoffTicks = rule.MinAgeDays > 0 ? nowUtc.AddDays(-rule.MinAgeDays).Ticks : long.MaxValue;
    }

    public bool Matches(ref FileSystemEntry entry)
    {
        if (entry.IsDirectory) return false;
        if (cutoffTicks != long.MaxValue && entry.LastWriteTimeUtc.UtcTicks >= cutoffTicks) return false;
        if (masks.Length == 0) return true;
        foreach (var mask in masks)
            if (FileSystemName.MatchesSimpleExpression(mask, entry.FileName, ignoreCase: true)) return true;
        return false;
    }

    public static EnumerationOptions Options(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        IgnoreInaccessible = true,
        // Links and cloud placeholders are never followed or counted, so a junction cannot lead a clean outside its folder.
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024
    };
}
