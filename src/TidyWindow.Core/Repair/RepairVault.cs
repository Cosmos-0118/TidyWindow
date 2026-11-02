using System.Diagnostics.CodeAnalysis;

namespace TidyWindow.Next.Core;

/// <summary>
/// The administrator helper's own copies of what a repair removed, kept in the protected store under tokens nobody can guess, and the quarantine folder beside them
/// for the folders it sets aside. The app's history holds the same copies for the Undo tab, but the helper puts back only what is here: a copy that arrives over the pipe,
/// or sits in the app's own files, could have been written by any program running as the ordinary account, and must never choose what is written with administrator rights.
/// A setup program the helper put in the package cache is noted here too, so it takes out only what it put in.
/// </summary>
public sealed class RepairVault(ProtectedStore store)
{
    /// <summary>How long a folder stays in the helper's quarantine, the same as in the app's own (<see cref="RepairHistory.QuarantineRetention"/>).</summary>
    public static readonly TimeSpan QuarantineRetention = RepairHistory.QuarantineRetention;
    /// <summary>How long a copy of a removed key or shortcut is kept. The Undo tab keeps a repair for as long, and a copy nothing names any more only takes up room.</summary>
    public static readonly TimeSpan RecordRetention = TimeSpan.FromDays(365);

    private sealed record Kept<T>(T Copy, DateTimeOffset When);
    private sealed record Staged(string Path, DateTimeOffset When);

    /// <summary>Where the helper keeps the folders it set aside: beside its records, where only administrators can write.</summary>
    public string Quarantine => Path.Combine(store.Folder, "SetAside");

    /// <summary>Where the helper copies an installer file before it checks and runs it, so the file cannot be swapped between the check and the run.</summary>
    public string Stage => Path.Combine(store.Folder, "Stage");

    /// <summary>
    /// Makes the store's folder, with its administrator-only permissions, before anything is kept under it: a subfolder made first would be owned by whoever made it,
    /// and the store would rightly move the whole folder aside as not its own.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">This process is not running as an administrator.</exception>
    /// <exception cref="IOException">The folder could not be made.</exception>
    public void Ready() => store.Save("ready", new Staged("", DateTimeOffset.UtcNow));

    public static string NewToken() => Guid.NewGuid().ToString("N");

    public static bool IsToken([NotNullWhen(true)] string? token) => token is { Length: 32 } && token.All(char.IsAsciiHexDigitLower);

    /// <summary>Keeps a copy under a new token and returns the token.</summary>
    /// <exception cref="IOException">The copy could not be kept.</exception>
    /// <exception cref="UnauthorizedAccessException">The store's folder could not be made one that administrators own.</exception>
    public string Keep<T>(T copy) { var token = NewToken(); Keep(token, copy); return token; }

    public void Keep<T>(string token, T copy) => store.Save(token, new Kept<T>(copy, DateTimeOffset.UtcNow));

    /// <summary>The copy kept under the token, or null when there is none (a token the helper never gave out, or a copy that is gone).</summary>
    public T? Take<T>(string? token) where T : class => IsToken(token) ? store.Load<Kept<T>>(token)?.Copy : null;

    public void Forget(string? token)
    {
        if (!IsToken(token)) return;
        try { store.Delete(token); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The change is made; a copy that lingers does no harm. */ }
    }

    /// <summary>Notes that the helper put a setup program at this path, so it may take that one out again later.</summary>
    public void MarkStaged(string path) => store.Save(FileId(path), new Staged(Path.GetFullPath(path), DateTimeOffset.UtcNow));

    public bool WasStaged(string path) => store.Load<Staged>(FileId(path)) is { } staged && string.Equals(staged.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    public void Unstage(string path) { try { store.Delete(FileId(path)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static string FileId(string path) => "file:" + Path.GetFullPath(path);

    /// <summary>
    /// Housekeeping for the helper's own folder: quarantined folders no record names any more or whose time is up, and records older than <see cref="RecordRetention"/>, are deleted.
    /// Only what lies directly in the store's folder is touched, never through a link, and only once the store has checked the folder is its own.
    /// </summary>
    public int Prune(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        // Exists runs the store's own check of the folder first: one that is not administrators' is moved aside, so nothing below is read from it.
        if (store.Exists("prune") || !Directory.Exists(store.Folder) || IsLink(new DirectoryInfo(store.Folder))) return 0;
        var pruned = 0;
        try
        {
            if (Directory.Exists(Quarantine) && !IsLink(new DirectoryInfo(Quarantine)))
                foreach (var folder in Directory.EnumerateDirectories(Quarantine))
                {
                    var token = Path.GetFileName(folder);
                    var kept = IsToken(token) ? store.Load<Kept<FolderBackup>>(token) : null;
                    if (kept is not null && at - kept.When <= QuarantineRetention) continue;
                    try
                    {
                        var info = new DirectoryInfo(folder);
                        if (IsLink(info)) Directory.Delete(folder, false);
                        else
                        {
                            foreach (var file in info.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                                if ((file.Attributes & FileAttributes.ReadOnly) != 0) file.Attributes &= ~FileAttributes.ReadOnly;
                            Directory.Delete(folder, true);
                        }
                        Forget(token);
                        pruned++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Tried again next time. */ }
                }
            foreach (var file in Directory.EnumerateFiles(store.Folder, "*.json"))
            {
                var info = new FileInfo(file);
                if (IsLink(info) || at - info.LastWriteTimeUtc <= RecordRetention) continue;
                try { info.Delete(); pruned++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            // A staging folder is emptied as soon as its run ends; one left behind came from a helper that was ended, and nothing refers to it.
            if (Directory.Exists(Stage) && !IsLink(new DirectoryInfo(Stage)))
                foreach (var folder in Directory.EnumerateDirectories(Stage))
                {
                    if (IsLink(new DirectoryInfo(folder)) || at - Directory.GetLastWriteTimeUtc(folder) <= TimeSpan.FromDays(1)) continue;
                    try { Directory.Delete(folder, true); pruned++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return pruned;
    }

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
}
