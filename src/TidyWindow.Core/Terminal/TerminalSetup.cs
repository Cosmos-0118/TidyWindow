using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>Where the setup reads and writes. Every path is in the person's own profile, so no administrator rights are involved.</summary>
public sealed record TerminalPaths(string Profile, string Starship, IReadOnlyList<string> TerminalSettings, IReadOnlyList<string> PsFzfFolders, string Backups)
{
    public static TerminalPaths ForCurrentUser()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new(
            Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
            Path.Combine(home, ".config", "starship.toml"),
            [
                Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
                Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
                Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json")
            ],
            [Path.Combine(documents, "PowerShell", "Modules", "PSFzf"), Path.Combine(programFiles, "PowerShell", "Modules", "PSFzf")],
            Path.Combine(local, "TidyWindow.Next", "Terminal"));
    }
}

public sealed record TerminalStatus(IReadOnlyList<TerminalTool> Missing, IReadOnlyList<TerminalTool> Added, bool ModuleAdded, bool PsFzf, bool Font, bool ProfileApplied, bool StarshipApplied, bool TerminalFound, bool TerminalApplied, DateTimeOffset? RestorePoint)
{
    public int ToolCount => TerminalKit.Tools.Count;
    public bool AllTools => Missing.Count == 0;
    /// <summary>Whether everything the setup writes is already as it would write it.</summary>
    public bool LookApplied => ProfileApplied && StarshipApplied && (TerminalApplied || !TerminalFound);
    public bool CanRestore => RestorePoint is not null;
}

public sealed record TerminalResult(bool Ok, string Message, string Detail = "");

/// <summary>
/// Gives PowerShell 7 and Windows Terminal a look: a profile, a Starship prompt and a Windows Terminal theme. Nothing is written before the
/// files that will change have been copied to a backup folder, and <see cref="Restore"/> puts every one of them back (deleting a file that did
/// not exist before). Installing the programs is not part of this: that goes through the install queue.
/// </summary>
public sealed class TerminalSetup(TerminalPaths paths, Func<string, bool>? commandExists = null, Func<bool>? fontInstalled = null)
{
    private const string ManifestName = "manifest.json";
    private const int KeptBackups = 10;
    private readonly Func<string, bool> commandExists = commandExists ?? CommandPath.Exists;
    private readonly Func<bool> fontInstalled = fontInstalled ?? WindowsFonts.HasCaskaydia;
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <param name="pending">WinGet ids that are waiting in the install queue, so an install that has not finished is not mistaken for one that failed.</param>
    public TerminalStatus Inspect(Func<string, bool>? pending = null)
    {
        var (added, module) = ReadInstalled(pending ?? (_ => false));
        var found = paths.TerminalSettings.Where(File.Exists).ToList();
        var folders = paths.TerminalSettings.Where(x => Directory.Exists(Path.GetDirectoryName(x))).ToList();
        return new(
            TerminalKit.Tools.Where(x => !commandExists(x.Command)).ToList(),
            added,
            module,
            paths.PsFzfFolders.Any(Directory.Exists),
            fontInstalled(),
            SameText(paths.Profile, TerminalKit.Profile),
            SameText(paths.Starship, TerminalKit.Starship),
            folders.Count > 0,
            folders.Count > 0 && found.Count == folders.Count && found.All(IsThemed),
            LatestRestorePoint()?.Created);
    }

    /// <summary>Backs up what is there, then writes the profile, the Starship config and the Windows Terminal theme.</summary>
    public TerminalResult Apply()
    {
        var targets = paths.TerminalSettings.Where(x => Directory.Exists(Path.GetDirectoryName(x))).ToList();
        var plan = new List<(string Path, string Text)>();
        if (!SameText(paths.Profile, TerminalKit.Profile)) plan.Add((paths.Profile, TerminalKit.Profile));
        if (!SameText(paths.Starship, TerminalKit.Starship)) plan.Add((paths.Starship, TerminalKit.Starship));
        foreach (var settings in targets)
        {
            string merged;
            try { merged = Merge(File.Exists(settings) ? File.ReadAllText(settings) : "{}"); }
            catch (JsonException ex) { return new(false, "Windows Terminal's settings could not be read, so nothing was changed.", settings + "\n" + ex.Message); }
            if (!File.Exists(settings) || !Equivalent(File.ReadAllText(settings), merged)) plan.Add((settings, merged));
        }
        if (plan.Count == 0) return new(true, "Your terminal already looks like this. Nothing was changed.");

        RestorePoint point;
        try { point = Backup(plan.Select(x => x.Path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, "The files could not be backed up, so nothing was changed.", ex.Message); }
        try
        {
            foreach (var (path, text) in plan) Write(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A half-finished setup is worse than none, so what was already written goes back.
            var undone = Undo(point);
            return new(false, "The terminal could not be set up, so nothing was changed." + (undone.Ok ? "" : " Some files may need to be restored by hand."), ex.Message + (undone.Ok ? "" : "\n" + undone.Detail));
        }
        Prune();
        var notes = new List<string>();
        if (targets.Count == 0) notes.Add("Windows Terminal has not been opened yet, so its theme was not set. Open it once and apply again.");
        return new(true, $"Your terminal is set up. Open a new PowerShell 7 tab to see it.", string.Join("\n", notes));
    }

    // ── What the setup installed, so it can be taken away again ──
    // Only programs that were missing when the setup queued them are remembered; anything already on the PC is never offered for removal.
    private string InstalledFile => Path.Combine(paths.Backups, "installed.json");
    private sealed record InstalledRecord(List<string> Tools, bool Module);

    private InstalledRecord LoadInstalled()
    {
        try { return JsonSerializer.Deserialize<InstalledRecord>(File.ReadAllText(InstalledFile)) ?? new([], false); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new([], false); }
    }

    private void SaveInstalled(InstalledRecord record)
    {
        try { Directory.CreateDirectory(paths.Backups); File.WriteAllText(InstalledFile, JsonSerializer.Serialize(record, Pretty)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Remembers that the setup is installing these, because they were missing.</summary>
    public void RememberInstalled(IEnumerable<TerminalTool> tools)
    {
        var record = LoadInstalled();
        foreach (var tool in tools) if (!record.Tools.Contains(tool.WingetId, StringComparer.OrdinalIgnoreCase)) record.Tools.Add(tool.WingetId);
        SaveInstalled(record);
    }

    private (IReadOnlyList<TerminalTool> Tools, bool Module) ReadInstalled(Func<string, bool> pending)
    {
        var record = LoadInstalled();
        // What is gone (removed by hand, or an install that failed) is forgotten, so it is never offered for removal later when something else puts it back.
        var kept = record.Tools.Where(id => TerminalKit.Tools.FirstOrDefault(t => t.WingetId.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } tool && (pending(id) || commandExists(tool.Command))).ToList();
        var module = record.Module && paths.PsFzfFolders.Any(Directory.Exists);
        if (kept.Count != record.Tools.Count || module != record.Module) SaveInstalled(new(kept, module));
        return (kept.Select(id => TerminalKit.Tools.First(t => t.WingetId.Equals(id, StringComparison.OrdinalIgnoreCase))).Where(t => commandExists(t.Command)).ToList(), module);
    }

    /// <summary>Puts back the files as they were before the last <see cref="Apply"/> that changed anything.</summary>
    public TerminalResult Restore()
    {
        var point = LatestRestorePoint();
        if (point is null) return new(false, "There is nothing to restore.");
        var result = Undo(point);
        if (result.Ok) MarkRestored(point);
        return result;
    }

    // ── Installing the module that powers Ctrl+R and Ctrl+T ──────
    public async Task<TerminalResult> InstallPsFzfAsync(ICommandRunner runner, IProgress<CommandOutput>? progress, CancellationToken token)
    {
        var pwsh = CommandPath.Find("pwsh");
        if (pwsh is null) return new(false, "PowerShell 7 is needed first. Install it, then try again.");
        var command = "$ProgressPreference='SilentlyContinue'; Install-Module PSFzf -Scope CurrentUser -Force -AllowClobber -Repository PSGallery; if (-not (Get-Module -ListAvailable PSFzf)) { exit 1 }";
        var result = await runner.RunAsync(new(pwsh, ["-NoProfile", "-NonInteractive", "-Command", command], TimeSpan.FromMinutes(5)) { Progress = progress }, token).ConfigureAwait(false);
        var detail = (result.Output + "\n" + result.Error).Trim();
        if (result.ExitCode == 0 && paths.PsFzfFolders.Any(Directory.Exists))
        {
            var record = LoadInstalled();
            SaveInstalled(record with { Module = true });
            return new(true, "PSFzf is installed.", detail);
        }
        return new(false, "PSFzf could not be installed. Check your connection and try again.", detail);
    }

    public async Task<TerminalResult> UninstallPsFzfAsync(ICommandRunner runner, IProgress<CommandOutput>? progress, CancellationToken token)
    {
        var pwsh = CommandPath.Find("pwsh");
        if (pwsh is null) return new(false, "PowerShell 7 is not installed, so PSFzf could not be removed from it.");
        var result = await runner.RunAsync(new(pwsh, ["-NoProfile", "-NonInteractive", "-Command", "$ProgressPreference='SilentlyContinue'; Uninstall-Module PSFzf -AllVersions -Force -ErrorAction Stop"], TimeSpan.FromMinutes(3)) { Progress = progress }, token).ConfigureAwait(false);
        var detail = (result.Output + "\n" + result.Error).Trim();
        if (result.ExitCode != 0 || paths.PsFzfFolders.Any(Directory.Exists)) return new(false, "PSFzf could not be removed. Close PowerShell windows that are using it and try again.", detail);
        SaveInstalled(LoadInstalled() with { Module = false });
        return new(true, "PSFzf was removed.", detail);
    }

    // ── Windows Terminal settings ────────────────────────────────
    private string Merge(string settingsText)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(settingsText) ? "{}" : settingsText, null, Lenient) as JsonObject ?? throw new JsonException("The settings are not a JSON object.");
        var kit = JsonNode.Parse(TerminalKit.WindowsTerminal)!.AsObject();
        foreach (var (key, value) in kit["settings"]!.AsObject()) root[key] = value!.DeepClone();
        foreach (var list in new[] { "schemes", "themes" })
        {
            var existing = root[list] as JsonArray ?? new JsonArray();
            foreach (var item in kit[list]!.AsArray())
            {
                var name = item!["name"]!.GetValue<string>();
                for (var i = existing.Count - 1; i >= 0; i--) if (existing[i]?["name"]?.GetValue<string>() == name) existing.RemoveAt(i);
                existing.Add(item.DeepClone());
            }
            root[list] = existing;
        }
        // Older files keep the profiles as a bare list; the settings for every profile live beside it.
        var profiles = root["profiles"] switch { JsonObject o => o, JsonArray a => new JsonObject { ["list"] = a.DeepClone() }, _ => new JsonObject() };
        var defaults = profiles["defaults"] as JsonObject ?? new JsonObject();
        foreach (var (key, value) in kit["defaults"]!.AsObject()) defaults[key] = value!.DeepClone();
        profiles["defaults"] = defaults;
        root["profiles"] = profiles;
        if (commandExists("pwsh")) root["defaultProfile"] = TerminalKit.PowerShell7ProfileId;
        return root.ToJsonString(Pretty);
    }

    private static bool Equivalent(string a, string b)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(a, null, Lenient), JsonNode.Parse(b, null, Lenient)); }
        catch (JsonException) { return false; }
    }

    private bool IsThemed(string settingsPath)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(settingsPath), null, Lenient);
            return root?["profiles"]?["defaults"]?["colorScheme"]?.GetValue<string>() == TerminalKit.SchemeName
                && root["theme"]?.GetValue<string>() == TerminalKit.SchemeName
                && root["schemes"] is JsonArray schemes && schemes.Any(x => x?["name"]?.GetValue<string>() == TerminalKit.SchemeName);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    // ── Backups ──────────────────────────────────────────────────
    private sealed record Entry(string Path, bool Existed, string? Copy);
    private sealed record RestorePoint(string Folder, DateTimeOffset Created, IReadOnlyList<Entry> Entries);

    private RestorePoint Backup(IEnumerable<string> files)
    {
        var created = DateTimeOffset.Now;
        var folder = Path.Combine(paths.Backups, created.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        var entries = new List<Entry>();
        var n = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file)) { entries.Add(new(file, false, null)); continue; }
            var copy = $"{++n}-{Path.GetFileName(file)}";
            File.Copy(file, Path.Combine(folder, copy), true);
            entries.Add(new(file, true, copy));
        }
        File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(new { created, restored = false, entries }, Pretty));
        return new(folder, created, entries);
    }

    private RestorePoint? LatestRestorePoint()
    {
        if (!Directory.Exists(paths.Backups)) return null;
        foreach (var folder in Directory.EnumerateDirectories(paths.Backups).OrderByDescending(x => x, StringComparer.Ordinal))
        {
            try
            {
                var manifest = Path.Combine(folder, ManifestName);
                if (!File.Exists(manifest)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = document.RootElement;
                if (root.GetProperty("restored").GetBoolean()) continue;
                var entries = root.GetProperty("entries").EnumerateArray()
                    .Select(x => new Entry(x.GetProperty("Path").GetString()!, x.GetProperty("Existed").GetBoolean(), x.GetProperty("Copy").GetString())).ToList();
                return new(folder, root.GetProperty("created").GetDateTimeOffset(), entries);
            }
            catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private TerminalResult Undo(RestorePoint point)
    {
        var failed = new List<string>();
        foreach (var entry in point.Entries)
        {
            try
            {
                if (entry.Existed) Put(entry.Path, Path.Combine(point.Folder, entry.Copy!));
                else if (File.Exists(entry.Path)) File.Delete(entry.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add($"{entry.Path}: {ex.Message}"); }
        }
        return failed.Count == 0 ? new(true, "Your earlier terminal setup is back.") : new(false, "Some files could not be put back.", string.Join("\n", failed));
    }

    private void MarkRestored(RestorePoint point)
    {
        try
        {
            File.WriteAllText(Path.Combine(point.Folder, ManifestName), JsonSerializer.Serialize(new { created = point.Created, restored = true, entries = point.Entries }, Pretty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Prune()
    {
        try
        {
            foreach (var old in Directory.EnumerateDirectories(paths.Backups).OrderByDescending(x => x, StringComparer.Ordinal).Skip(KeptBackups)) Directory.Delete(old, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ── Files ────────────────────────────────────────────────────
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tidywindow-new";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    /// <summary>Puts a saved copy back byte for byte, so a file's encoding survives the round trip.</summary>
    private static void Put(string path, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tidywindow-new";
        File.Copy(source, temp, true);
        File.Move(temp, path, true);
    }

    private static bool SameText(string path, string text)
    {
        try { return File.Exists(path) && Normalize(File.ReadAllText(path)) == Normalize(text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}

/// <summary>Finds programs the way a new terminal would: through the PATH as Windows has it now, not as it was when TidyWindow started.</summary>
public static class CommandPath
{
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ".com"];

    public static bool Exists(string command) => Find(command) is not null;

    public static string? Find(string command)
    {
        foreach (var folder in Folders())
        {
            foreach (var extension in Extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder, command + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static IEnumerable<string> Folders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Process })
            foreach (var raw in (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var folder = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
                if (folder.Length > 0 && seen.Add(folder)) yield return folder;
            }
    }
}

public static class WindowsFonts
{
    /// <summary>Whether a Caskaydia Cove Nerd Font Mono is installed, for this account or for everyone.</summary>
    public static bool HasCaskaydia()
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var (hive, key) in new[] { (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"), (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts") })
        {
            try
            {
                using var fonts = hive.OpenSubKey(key);
                if (fonts?.GetValueNames().Any(x => x.Contains("Caskaydia", StringComparison.OrdinalIgnoreCase) && x.Contains("Mono", StringComparison.OrdinalIgnoreCase) || x.Contains("CaskaydiaCove NFM", StringComparison.OrdinalIgnoreCase)) == true) return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return false;
    }
}
