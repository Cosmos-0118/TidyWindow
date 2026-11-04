using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// Where the administrator helper keeps the copies it needs to put things back: the command of a startup item it removed, the registry
/// values a repair deleted, the settings a tweak replaced. The folder lives under ProgramData with permissions only administrators can
/// change, so a program running as the ordinary account cannot edit a saved copy and have the helper write something else back with
/// administrator rights. The app keeps its own copy of the same records for display; the helper trusts only this one.
/// A folder or file in the way that administrators do not own (one made before the first elevated run) is moved aside and never read.
/// Every call checks the folder again, so a folder swapped between two calls is noticed too.
/// </summary>
public sealed class ProtectedStore
{
    public const string FolderName = "TidyWindow.Next";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string root;
    private readonly bool enforce;

    /// <param name="area">The feature that owns the records; it becomes a subfolder name.</param>
    /// <param name="root">Where the store lives. The default is %ProgramData%\TidyWindow.Next.</param>
    /// <param name="enforce">Whether folders must be owned by administrators to be used. Tests run without administrator rights and turn this off.</param>
    public ProtectedStore(string area, string? root = null, bool enforce = true)
    {
        if (area.Length == 0 || area.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("The area name must be letters, digits and dashes.", nameof(area));
        this.root = root ?? DefaultRoot;
        this.enforce = enforce;
        Folder = Path.Combine(this.root, area);
    }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);

    /// <summary>The folder that holds this area's records.</summary>
    public string Folder { get; }

    /// <summary>Keeps a copy under <paramref name="id"/>, replacing an earlier one.</summary>
    /// <exception cref="UnauthorizedAccessException">This process cannot make the folder one that administrators own, because it is not running as one.</exception>
    /// <exception cref="IOException">The folder could not be made or the file could not be written.</exception>
    public void Save<T>(string id, T value)
    {
        Prepare(create: true);
        var file = FileFor(id);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Envelope<T>(Normalize(id), DateTimeOffset.UtcNow, value), Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>The copy kept under <paramref name="id"/>, or null when there is none or it cannot be trusted.</summary>
    public T? Load<T>(string id) where T : class
    {
        if (!Prepare(create: false)) return null;
        var file = FileFor(id);
        if (!File.Exists(file)) return null;
        if (enforce && OperatingSystem.IsWindows() && !IsOurFile(file)) { MoveAside(file); return null; }
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<T>>(File.ReadAllText(file), Json);
            return envelope is not null && string.Equals(envelope.Id, Normalize(id), StringComparison.Ordinal) ? envelope.Value : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Forgets the copy kept under <paramref name="id"/>. Returns false when there was none.</summary>
    public bool Delete(string id)
    {
        if (!Prepare(create: false)) return false;
        var file = FileFor(id);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    public bool Exists(string id) => Prepare(create: false) && File.Exists(FileFor(id));

    private static string Normalize(string id) => id.Trim().ToLowerInvariant();

    private string FileFor(string id) => Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(id)))) + ".json");

    /// <summary>Makes sure both folders are ones administrators own. Returns false when they do not exist and were not to be made.</summary>
    private bool Prepare(bool create)
    {
        foreach (var path in new[] { root, Folder })
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                if (!enforce || !OperatingSystem.IsWindows() || IsOurFolder(path)) continue;
                MoveAside(path);
            }
            if (!create) return false;
            if (enforce && OperatingSystem.IsWindows()) CreateProtected(path);
            else Directory.CreateDirectory(path);
        }
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOurFolder(string path)
    {
        var info = new DirectoryInfo(path);
        // A file, a link or a mount point where the folder should be is never ours.
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        DirectorySecurity security;
        try { security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { return false; }
        return Problem(security) is null;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOurFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        try { return IsAdministrator(info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { return false; }
    }

    /// <summary>Why a folder's permissions cannot be trusted, or null when administrators own it and no other account can change it.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? Problem(DirectorySecurity security)
    {
        const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (!IsAdministrator(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) return "The folder is not owned by administrators.";
        if (!security.AreAccessRulesProtected) return "The folder takes its permissions from its parent.";
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var who = (SecurityIdentifier)rule.IdentityReference;
            if (IsAdministrator(who)) continue;
            if ((rule.FileSystemRights & WriteRights) != 0) return $"{who} can change the folder.";
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator(SecurityIdentifier? sid) =>
        sid is not null && (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid));

    [SupportedOSPlatform("windows")]
    private static void CreateProtected(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(administrators);
        // Made with its permissions in one step, so there is no moment when the folder exists with permissions anyone could use.
        new DirectoryInfo(path).Create(security);
        if (!IsOurFolder(path)) throw new UnauthorizedAccessException("The protected folder could not be given administrator-only permissions.");
    }

    private static void MoveAside(string path)
    {
        var aside = path + ".untrusted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        if (Directory.Exists(path)) Directory.Move(path, aside);
        else File.Move(path, aside);
    }

    private sealed record Envelope<T>(string Id, DateTimeOffset When, T Value);
}
