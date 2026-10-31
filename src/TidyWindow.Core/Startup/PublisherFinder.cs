using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>Where a publisher name came from, so the screen can say how far to trust it.</summary>
public enum PublisherSource { None, FileProperties, Package, Signature, InstalledApp, TaskAuthor }

public sealed record FoundPublisher(string Name, PublisherSource Source)
{
    public static readonly FoundPublisher None = new("", PublisherSource.None);
}

/// <summary>Turns the raw text Windows hands back (version info, certificates, task authors) into a name worth showing, or nothing.</summary>
public static partial class PublisherNames
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // What programs and installers put there when nobody filled it in.
    [GeneratedRegex(@"^[<(\[]?\s*(unknown|n/?a|none|null|not available|not specified|company( name)?|your company( name)?|publisher|manufacturer|vendor|author|owner|copyright.*|\(c\)|c|-+|\.+|_+|\?+)\s*[>)\]]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    // Certificates Microsoft signs on behalf of other companies' drivers and parts. The signer is not the publisher there.
    [GeneratedRegex(@"hardware compatibility|third.?party|3rd party|attestation|early launch|hardware abstraction|test (root|sign)|code signing pca|development", RegexOptions.IgnoreCase)]
    private static partial Regex OnBehalfSigner();

    /// <summary>The text as a name, or "" when it is blank, a placeholder, a resource reference, an account name or an e-mail address.</summary>
    public static string Tidy(string? text)
    {
        var name = Whitespace().Replace(text ?? "", " ").Trim().Trim('"', '\'').Trim().TrimEnd(',', ';').Trim();
        if (name.Length < 2 || name.Length > 120) return "";
        if (name[0] == '@' || name.StartsWith("$(", StringComparison.Ordinal) || name.Contains("ms-resource:", StringComparison.OrdinalIgnoreCase)) return "";
        // DOMAIN\user, a %variable%, a SID or an e-mail address is who made a task, not a company.
        if (name.Contains('\\') || name.Contains('%') || name.StartsWith("S-1-5-", StringComparison.OrdinalIgnoreCase)) return "";
        if (name.Contains('@') && !name.Contains(' ')) return "";
        return Placeholder().IsMatch(name) ? "" : name;
    }

    /// <summary>The company named by a certificate subject such as <c>CN="Anthropic, PBC", O="Anthropic, PBC", L=San Francisco</c>.</summary>
    public static string FromSubject(string? subject)
    {
        var parts = ParseSubject(subject ?? "");
        parts.TryGetValue("CN", out var common);
        parts.TryGetValue("O", out var organization);
        if (OnBehalfSigner().IsMatch(common ?? "")) return "";
        var name = Tidy(organization);
        return name.Length > 0 ? name : Tidy(common);
    }

    /// <summary>Reads a distinguished name: <c>KEY=value</c> pairs split by commas, where a value may be quoted or escaped to hold a comma. The first value of a key wins.</summary>
    private static Dictionary<string, string> ParseSubject(string subject)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < subject.Length)
        {
            var equals = subject.IndexOf('=', i);
            if (equals < 0) break;
            var key = subject[i..equals].Trim();
            i = equals + 1;
            while (i < subject.Length && subject[i] == ' ') i++;
            var value = new StringBuilder();
            if (i < subject.Length && subject[i] == '"')
            {
                i++;
                for (; i < subject.Length; i++)
                {
                    if (subject[i] == '"') { if (i + 1 < subject.Length && subject[i + 1] == '"') { value.Append('"'); i++; continue; } i++; break; }
                    value.Append(subject[i]);
                }
                while (i < subject.Length && subject[i] != ',' && subject[i] != '+' && subject[i] != ';') i++;
            }
            else
            {
                for (; i < subject.Length && subject[i] != ',' && subject[i] != '+' && subject[i] != ';'; i++)
                {
                    if (subject[i] == '\\' && i + 1 < subject.Length) i++;
                    value.Append(subject[i]);
                }
            }
            i++;
            if (key.Length > 0) parts.TryAdd(key, value.ToString().Trim());
        }
        return parts;
    }
}

/// <summary>
/// Finds who is behind a startup item by asking every source Windows keeps, strongest first:
/// the file's own details, the Store package, the digital signature (only when it checks out), the installed-apps list, and last the author of a scheduled task.
/// The last two can be written by anyone, so they never claim a file is from Microsoft unless its signature checks out.
/// </summary>
public sealed partial class PublisherFinder(IStartupSystem system)
{
    private sealed record Program(string Name, string Publisher);

    private static readonly string[] SharedFolders =
    [
        "users", "public", "program files", "program files (x86)", "programdata", "windows", "system32", "syswow64", "common files", "appdata", "local", "locallow", "roaming",
        "programs", "temp", "downloads", "desktop", "documents", "start menu", "windowsapps", "microsoft"
    ];

    private readonly Lazy<(Dictionary<string, List<Program>> ByFolder, Dictionary<string, List<Program>> ByName)> installed = new(() => Index(system));
    private readonly Dictionary<string, string> packages = new(StringComparer.OrdinalIgnoreCase);

    public FoundPublisher Find(string name, string exePath, FileFacts file, string hint = "", bool hintIsPackage = false)
    {
        // A Store package's manifest is vetted by the Store and cannot be edited afterwards, so it beats what the program says about itself
        // (WhatsApp's own file claims its company is "WhatsApp.Root").
        if (Tidy(hintIsPackage ? hint : "") is { Length: > 0 } fromHint) return new(fromHint, PublisherSource.Package);
        if (exePath.Length > 0 && Tidy(PackageOf(exePath)) is { Length: > 0 } package) return new(package, PublisherSource.Package);

        // Some programs put their own file name in the company field.
        var company = Tidy(file.Company);
        if (company.Length > 0 && !string.Equals(company, Path.GetFileNameWithoutExtension(exePath), StringComparison.OrdinalIgnoreCase)) return new(company, PublisherSource.FileProperties);

        if (file.Trust == StartupTrust.Verified && Tidy(file.Signer) is { Length: > 0 } signer) return new(signer, PublisherSource.Signature);

        // Anyone can write these, so a claim of being Microsoft only counts once the file is really signed.
        var trusted = file.Trust == StartupTrust.Verified;
        if (Weak(FromInstalledApps(name, exePath), trusted) is { Length: > 0 } app) return new(app, PublisherSource.InstalledApp);
        if (Weak(hintIsPackage ? "" : hint, trusted) is { Length: > 0 } author) return new(author, PublisherSource.TaskAuthor);
        return FoundPublisher.None;
    }

    private static string Tidy(string? text) => PublisherNames.Tidy(text);

    private static string Weak(string? text, bool trusted) =>
        Tidy(text) is { Length: > 0 } name && (trusted || !name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) ? name : "";

    private string PackageOf(string exePath)
    {
        if (packages.TryGetValue(exePath, out var known)) return known;
        return packages[exePath] = system.PackagePublisher(exePath);
    }

    private string FromInstalledApps(string name, string exePath)
    {
        var (byFolder, byName) = installed.Value;

        // The program lives in (or under) a folder an installer registered: the deepest such folder is the most specific one.
        for (var folder = FolderOf(exePath); folder is { Length: > 0 }; folder = ParentOf(folder))
        {
            if (!byFolder.TryGetValue(folder, out var here)) continue;
            if (Choose(here, name) is { Length: > 0 } chosen) return chosen;
        }

        return NameKey(name) is { Length: >= 3 } key && byName.TryGetValue(key, out var named) ? Choose(named, name) : "";
    }

    /// <summary>One publisher if the candidates agree, else the one whose name matches the item, else nothing rather than a guess.</summary>
    private static string Choose(List<Program> candidates, string name)
    {
        var publishers = candidates.Select(x => x.Publisher).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (publishers.Count == 1) return publishers[0];
        var key = NameKey(name);
        return key.Length >= 3 && candidates.FirstOrDefault(x => NameKey(x.Name) == key) is { } same ? same.Publisher : "";
    }

    private static (Dictionary<string, List<Program>>, Dictionary<string, List<Program>>) Index(IStartupSystem system)
    {
        var byFolder = new Dictionary<string, List<Program>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<Program>>(StringComparer.OrdinalIgnoreCase);
        void Put(Dictionary<string, List<Program>> map, string key, Program program)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(program);
        }

        foreach (var entry in system.InstalledPrograms())
        {
            if (entry.IsUpdate || entry.Name.Length == 0 || Tidy(entry.Publisher) is not { Length: > 0 } publisher) continue;
            var program = new Program(entry.Name, publisher);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Clean(system, entry.InstallLocation) is { Length: > 0 } location) folders.Add(location);
            foreach (var command in new[] { entry.DisplayIcon, entry.UninstallString, entry.QuietUninstallString })
                if (FolderOf(Clean(system, IconIndex().Replace(command, ""), command: true)) is { Length: > 0 } folder) folders.Add(folder);
            foreach (var folder in folders.Where(x => Usable(system.Places, x))) Put(byFolder, folder, program);
            if (NameKey(entry.Name) is { Length: >= 3 } key) Put(byName, key, program);
        }
        return (byFolder, byName);
    }

    [GeneratedRegex(@",\s*-?\d+\s*$")]
    private static partial Regex IconIndex();

    private static string Clean(IStartupSystem system, string text, bool command = false)
    {
        var path = command ? FirstProgram(system, text) : system.Expand(text.Trim().Trim('"'));
        return path.Replace('/', '\\').TrimEnd('\\');
    }

    private static string FirstProgram(IStartupSystem system, string command) =>
        InstallRepair.Candidates(command).Select(x => system.Expand(x.Trim().Trim('"'))).FirstOrDefault(x => x.Length > 0) ?? "";

    /// <summary>A folder an installer names is only evidence if it belongs to that one program, not to everything in Program Files, the profile or Windows.</summary>
    private static bool Usable(StartupPlaces places, string folder)
    {
        var parts = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || Array.IndexOf(SharedFolders, parts[^1].ToLowerInvariant()) >= 0) return false;
        if (string.Equals(folder, places.UserProfile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        return !places.InWindows(folder + "\\x") && !places.InScratch(folder + "\\x");
    }

    private static string FolderOf(string path)
    {
        if (path.Length == 0 || path.IndexOf('\\') < 0) return "";
        var folder = path.Replace('/', '\\').TrimEnd('\\');
        var slash = folder.LastIndexOf('\\');
        return slash > 0 ? folder[..slash] : "";
    }

    private static string ParentOf(string folder) => folder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length <= 2 ? "" : FolderOf(folder);

    [GeneratedRegex(@"\(\s*(x64|x86|32-bit|64-bit)\s*\)|\b(x64|x86)\b|\bv?\d+(\.\d+)+\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionNoise();

    /// <summary>A name boiled down to letters and digits without its version or bitness, so "Discord" and "Discord 1.0.9" can meet.</summary>
    private static string NameKey(string name)
    {
        var plain = VersionNoise().Replace(name, "");
        var key = new StringBuilder();
        foreach (var c in plain) if (char.IsLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
        return key.ToString();
    }
}
