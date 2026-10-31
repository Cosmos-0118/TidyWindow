using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public static partial class PackageOutputParser
{
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-/]*$")]
    private static partial Regex Identifier();
    [GeneratedRegex(@"\S(?:.*?\S)?(?= {2,}|$)")]
    private static partial Regex Columns();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Words();

    public static string Clean(string text) => Ansi().Replace(text, "").Replace("\b", "");
    // WinGet names what it did not install from a source by where Windows keeps it: the registry's uninstall list (ARP) or a Store package (MSIX).
    // Registry key names are free text, so the characters allowed are a fixed set with no quotes, pipes, percent signs or redirection.
    [GeneratedRegex(@"^(?:ARP\\(?:Machine|User)\\X(?:64|86)|MSIX)\\[A-Za-z0-9 ._+\-{}()&,@~=#!\\]+$")]
    private static partial Regex WindowsIdentifier();

    // WinGet pads a column to its widest cell plus one space, so when every name is short the header reads "Name Id" with a single space.
    // Splitting on runs of spaces would then merge those two columns; the labels are single words, so fall back to them.
    internal static int[] HeaderColumns(string header)
    {
        var words = Words().Matches(header);
        var spaced = Columns().Matches(header);
        var found = words.Count is >= 3 and <= 5 && words.Count > spaced.Count ? words : spaced;
        return found.Select(m => CellWidth(header[..m.Index])).ToArray();
    }
    public static bool IsSafeId(string id) => id.Length is > 0 and <= 256 && Identifier().IsMatch(id) && !id.Contains("..");
    public static bool IsWindowsId(string id) => id.Length is > 0 and <= 256 && WindowsIdentifier().IsMatch(id) && !id.Contains("..");
    public static bool IsSafeInstalledId(string id) => IsSafeId(id) || IsWindowsId(id);

    /// <param name="windowsApps">Keep the apps WinGet found on the PC but did not install (registry programs and Store apps), and read each row's source.</param>
    public static IReadOnlyList<Package> Parse(ManagerKind manager, string output, int limit = 60, bool windowsApps = false)
    {
        var lines = Clean(output).Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
        var result = new List<Package>();
        if (manager == ManagerKind.Chocolatey)
        {
            foreach (var line in lines)
            {
                var fields = line.Trim().Split('|');
                if (fields.Length == 2 && IsSafeId(fields[0]) && fields[1].Length > 0)
                    result.Add(new(manager, fields[0], fields[0], fields[1], "chocolatey"));
            }
        }
        else
        {
            // Derive column boundaries from the CLI's header, independent of translated labels.
            for (var i = 1; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"^\s*-{2,}[\s-]*$")) continue;
                var dashed = Regex.Matches(lines[i], @"-{2,}");
                var columns = dashed.Count >= 3
                    ? dashed.Select(m => CellWidth(lines[i][..m.Index])).ToArray()
                    : HeaderColumns(lines[i - 1]);
                if (columns.Length < 3) continue;
                foreach (var row in lines.Skip(i + 1))
                {
                    if (string.IsNullOrWhiteSpace(row)) continue;
                    var fields = columns.Select((start, index) => SliceCells(row, start, index + 1 < columns.Length ? columns[index + 1] : int.MaxValue).Trim()).ToArray();
                    var name = fields[0];
                    var id = manager == ManagerKind.Winget ? fields[1] : fields[2] + "/" + name;
                    var version = manager == ManagerKind.Winget ? fields[2] : fields[1];
                    if (!(windowsApps ? IsSafeInstalledId(id) : IsSafeId(id)) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) continue;
                    // The source is the last column, and blank for apps WinGet did not install.
                    var source = manager != ManagerKind.Winget ? fields[2] : windowsApps && columns.Length >= 4 ? fields[^1] : "winget";
                    result.Add(new(manager, id, name, version, source));
                }
                break;
            }
        }
        return result.DistinctBy(x => x.Key).Take(limit).ToArray();
    }

    // WinGet aligns output in terminal cells, not UTF-16 offsets (e.g. CJK names).
    private static int RuneWidth(Rune rune)
    {
        var c = rune.Value;
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return 0;
        return c >= 0x1100 && (c <= 0x115f || c is 0x2329 or 0x232a || (c >= 0x2e80 && c <= 0xa4cf) ||
            (c >= 0xac00 && c <= 0xd7a3) || (c >= 0xf900 && c <= 0xfaff) || (c >= 0xfe10 && c <= 0xfe6f) ||
            (c >= 0xff00 && c <= 0xff60) || (c >= 0xffe0 && c <= 0xffe6) || (c >= 0x1f300 && c <= 0x1faff) || c >= 0x20000) ? 2 : 1;
    }
    internal static int CellWidth(string text) => text.EnumerateRunes().Sum(RuneWidth);
    internal static string SliceCells(string text, int start, int end)
    {
        var result = new StringBuilder();
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (position >= start && position < end) result.Append(rune.ToString());
            position += RuneWidth(rune);
            if (position >= end) break;
        }
        return result.ToString();
    }
}
