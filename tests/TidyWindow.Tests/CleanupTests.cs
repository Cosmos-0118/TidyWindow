using System.Diagnostics;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class CleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, string> variables;

    public CleanupTests()
    {
        var profile = Path.Combine(root, "Users", "me");
        variables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = profile, ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local"), ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
            ["TEMP"] = Path.Combine(profile, "AppData", "Local", "Temp"), ["WINDIR"] = Path.Combine(root, "Windows"), ["PROGRAMDATA"] = Path.Combine(root, "ProgramData"),
            ["ProgramFiles"] = Path.Combine(root, "Program Files"), ["SYSTEMDRIVE"] = root
        };
        Directory.CreateDirectory(root);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private CleanupContext Context(bool admin = false) => new()
    {
        Variable = name => variables.GetValueOrDefault(name), IsAdministrator = admin, Now = () => Now, RunningProcesses = () => new HashSet<string>(), Parallelism = 4
    };

    private string Make(string relative, int bytes = 100, int ageDays = 0, bool readOnly = false)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays));
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    private static CleanupRule Rule(string id, string[] paths, string? files = null, int age = 0, bool recurse = true, bool quarantine = false, bool admin = false, CleanupLevel level = CleanupLevel.Recommended) =>
        new() { Id = id, Group = "Test", Name = id, Paths = paths, Files = files, MinAgeDays = age, Recurse = recurse, Quarantine = quarantine, Admin = admin, Level = level };

    private async Task<CleanupFinding> ScanOne(CleanupRule rule, bool admin = false) => (await new CleanupScanner(Context(admin)).ScanAsync([rule])).Single();

    [Fact]
    public void CatalogLoadsAndEveryRuleIsWellFormed()
    {
        var rules = CleanupCatalog.Load();
        Assert.True(rules.Count >= 30);
        Assert.Equal(rules.Count, rules.Select(x => x.Id).Distinct().Count());
        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.False(string.IsNullOrWhiteSpace(rule.Detail));
            if (rule.Action == CleanupAction.Files) Assert.NotEmpty(rule.Paths);
            Assert.All(rule.Paths, path => Assert.StartsWith("%", path));
            if (rule.Quarantine) Assert.Equal(CleanupLevel.Review, rule.Level);
        }
    }

    [Fact]
    public void EveryCatalogFolderPassesTheSafetyGuardOnAStandardLayout()
    {
        var context = Context();
        var protectedPaths = CleanupPaths.Protected(context);
        foreach (var rule in CleanupCatalog.Load())
            foreach (var pattern in rule.Paths)
            {
                var expanded = CleanupPaths.Expand(pattern, context);
                if (expanded is null) continue;
                var concrete = expanded.Replace("*", "Default");
                Assert.True(CleanupPaths.IsSafeRoot(concrete, rule, protectedPaths), $"{rule.Id}: {pattern}");
            }
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Windows\\System32")]
    [InlineData("Users")]
    [InlineData("Users\\me")]
    [InlineData("Users\\me\\Documents")]
    [InlineData("Users\\me\\Desktop")]
    [InlineData("Users\\me\\AppData\\Local")]
    [InlineData("Users\\me\\Downloads")]
    [InlineData("Program Files")]
    public void TheGuardRefusesFoldersThatAreNeverCleanedWhole(string relative)
    {
        var rule = Rule("bad", ["x"]);
        Assert.False(CleanupPaths.IsSafeRoot(Path.Combine(root, relative), rule, CleanupPaths.Protected(Context())));
    }

    [Fact]
    public void TheGuardRefusesDrivesAndAnythingThatContainsAProtectedFolder()
    {
        var rule = Rule("bad", ["x"]);
        var protectedPaths = CleanupPaths.Protected(Context());
        Assert.False(CleanupPaths.IsSafeRoot(Path.GetPathRoot(root)!, rule, protectedPaths));
        Assert.False(CleanupPaths.IsSafeRoot(root, rule, protectedPaths));
        Assert.True(CleanupPaths.IsSafeRoot(Path.Combine(root, "Users", "me", "AppData", "Local", "Temp"), rule, protectedPaths));
    }

    [Fact]
    public void NamedFileRulesMayLiveDirectlyInAProtectedFolderButWholeFolderRulesMayNot()
    {
        var protectedPaths = CleanupPaths.Protected(Context());
        var downloads = Path.Combine(root, "Users", "me", "Downloads");
        Assert.True(CleanupPaths.IsSafeRoot(downloads, Rule("a", ["x"], files: "*.exe", recurse: false), protectedPaths));
        Assert.False(CleanupPaths.IsSafeRoot(downloads, Rule("b", ["x"], files: "*.exe"), protectedPaths));
        Assert.False(CleanupPaths.IsSafeRoot(downloads, Rule("c", ["x"], files: "*", recurse: false), protectedPaths));
    }

    [Fact]
    public async Task ScanCountsExactlyTheFilesACleanWouldRemove()
    {
        Make("Users/me/AppData/Local/Temp/a.tmp", 100, ageDays: 5);
        Make("Users/me/AppData/Local/Temp/sub/b.tmp", 250, ageDays: 5);
        Make("Users/me/AppData/Local/Temp/sub/deep/c.tmp", 50, ageDays: 5);
        Make("Users/me/AppData/Local/Temp/fresh.tmp", 999, ageDays: 0);
        var finding = await ScanOne(Rule("temp", ["%TEMP%"], age: 1));
        Assert.Equal(3, finding.Files);
        Assert.Equal(400, finding.Bytes);
    }

    [Fact]
    public async Task MasksAndNonRecursiveRulesOnlyMatchWhatTheyName()
    {
        Make("Users/me/Downloads/setup.exe", 10, ageDays: 90);
        Make("Users/me/Downloads/notes.txt", 20, ageDays: 90);
        Make("Users/me/Downloads/new.exe", 30, ageDays: 2);
        Make("Users/me/Downloads/nested/old.exe", 40, ageDays: 90);
        var finding = await ScanOne(Rule("installers", ["%USERPROFILE%\\Downloads"], files: "*.exe;*.msi", age: 60, recurse: false, quarantine: true, level: CleanupLevel.Review));
        Assert.Equal(1, finding.Files);
        Assert.Equal(10, finding.Bytes);
    }

    [Fact]
    public async Task WildcardFoldersCoverEveryProfile()
    {
        Make("Users/me/AppData/Local/Browser/User Data/Default/Cache/x", 10);
        Make("Users/me/AppData/Local/Browser/User Data/Profile 1/Cache/y", 20);
        Make("Users/me/AppData/Local/Browser/User Data/Profile 1/History", 999);
        var finding = await ScanOne(Rule("browser", ["%LOCALAPPDATA%\\Browser\\User Data\\*\\Cache"]));
        Assert.Equal(2, finding.Roots.Length);
        Assert.Equal(30, finding.Bytes);
    }

    [Fact]
    public async Task MissingFoldersAndUnknownVariablesFindNothingWithoutErrors()
    {
        var finding = await ScanOne(Rule("none", ["%LOCALAPPDATA%\\Nothing", "%NOT_SET%\\x"]));
        Assert.False(finding.HasContent);
        Assert.Empty(finding.Roots);
    }

    [Fact]
    public async Task AdministratorAreasAreMeasuredWithoutElevationButFlaggedAndNeverCleanedWithoutIt()
    {
        var file = Make("Windows/Temp/a.tmp", 100, ageDays: 3);
        var rule = Rule("windows-temp", ["%WINDIR%\\Temp"], admin: true);
        var seen = await ScanOne(rule, admin: false);
        Assert.True(seen.NeedsAdmin);
        Assert.Equal(100, seen.Bytes);
        var result = await new CleanupCleaner(Context(admin: false)).CleanAsync([seen]);
        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(file));
        Assert.False((await ScanOne(rule, admin: true)).NeedsAdmin);
    }

    [Fact]
    public async Task FoldersThatAnotherRuleAlreadyCleansAreNotCountedTwice()
    {
        Make("Users/me/AppData/Local/Temp/a", 100);
        Make("Users/me/AppData/Local/Temp/Inner/b", 50);
        var outer = Rule("temp", ["%TEMP%"]);
        var inner = Rule("inner", ["%TEMP%\\Inner"]);
        var findings = await new CleanupScanner(Context()).ScanAsync([outer, inner]);
        Assert.Equal(150, findings.Sum(x => x.Bytes));
        Assert.Empty(findings[1].Roots);
    }

    [Fact]
    public async Task JunctionsAreNeverFollowedOutOfAFolder()
    {
        var outside = Path.Combine(root, "outside");
        Make("outside/precious.doc", 777);
        var temp = Path.Combine(root, "Users", "me", "AppData", "Local", "Temp");
        Make("Users/me/AppData/Local/Temp/own.tmp", 10);
        if (!TryLink(Path.Combine(temp, "link"), outside)) return;
        var rule = Rule("temp", ["%TEMP%"]);
        var finding = await ScanOne(rule);
        Assert.Equal(10, finding.Bytes);
        await new CleanupCleaner(Context()).CleanAsync([finding]);
        Assert.True(File.Exists(Path.Combine(outside, "precious.doc")));
    }

    [Fact]
    public async Task CleaningRemovesFilesAndEmptiedFoldersButKeepsTheRootAndYoungFiles()
    {
        var temp = Path.Combine(root, "Users", "me", "AppData", "Local", "Temp");
        Make("Users/me/AppData/Local/Temp/a.tmp", 100, ageDays: 5);
        Make("Users/me/AppData/Local/Temp/old/b.tmp", 200, ageDays: 5, readOnly: true);
        Make("Users/me/AppData/Local/Temp/mixed/old.tmp", 300, ageDays: 5);
        Make("Users/me/AppData/Local/Temp/mixed/new.tmp", 400, ageDays: 0);
        foreach (var folder in Directory.EnumerateDirectories(temp, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(folder, Now.AddDays(-5));
        var rule = Rule("temp", ["%TEMP%"], age: 1);
        var finding = await ScanOne(rule);
        var result = await new CleanupCleaner(Context()).CleanAsync([finding]);
        Assert.Equal(finding.Bytes, result.FreedBytes);
        Assert.Equal(3, result.Removed);
        Assert.Equal(0, result.Skipped);
        Assert.True(Directory.Exists(temp));
        Assert.False(Directory.Exists(Path.Combine(temp, "old")));
        Assert.True(File.Exists(Path.Combine(temp, "mixed", "new.tmp")));
        Assert.False(File.Exists(Path.Combine(temp, "mixed", "old.tmp")));
        Assert.False(File.Exists(Path.Combine(temp, "a.tmp")));
    }

    [Fact]
    public async Task AFileThatIsOpenIsSkippedAndCountedNotForced()
    {
        var open = Make("Users/me/AppData/Local/Temp/busy.tmp", 100, ageDays: 5);
        Make("Users/me/AppData/Local/Temp/free.tmp", 50, ageDays: 5);
        var finding = await ScanOne(Rule("temp", ["%TEMP%"], age: 1));
        using var hold = new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await new CleanupCleaner(Context()).CleanAsync([finding]);
        Assert.Equal(1, result.InUse);
        Assert.Equal(50, result.FreedBytes);
        Assert.True(File.Exists(open));
    }

    [Fact]
    public async Task CleaningRefusesAFolderThatBecameUnsafeAfterTheScan()
    {
        var documents = Path.Combine(root, "Users", "me", "Documents");
        Make("Users/me/Documents/thesis.docx", 500);
        var rule = Rule("sneaky", ["x"]);
        var forged = new CleanupFinding(rule, [documents], 500, 1, 0, false);
        var result = await new CleanupCleaner(Context()).CleanAsync([forged]);
        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(Path.Combine(documents, "thesis.docx")));
    }

    [Fact]
    public async Task HeldFilesMoveOutAndComeBackToTheSamePlace()
    {
        var installer = Make("Users/me/Downloads/setup.exe", 321, ageDays: 90);
        var rule = Rule("installers", ["%USERPROFILE%\\Downloads"], files: "*.exe", age: 60, recurse: false, quarantine: true, level: CleanupLevel.Review);
        var finding = await ScanOne(rule);
        var quarantine = new CleanupQuarantine(Path.Combine(root, "undo"), () => Now);
        var result = await new CleanupCleaner(Context(), quarantine).CleanAsync([finding]);
        Assert.False(File.Exists(installer));
        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(321, result.HeldBytes);
        Assert.NotNull(result.UndoId);
        var restored = quarantine.Restore(result.UndoId!);
        Assert.Equal(1, restored.Restored);
        Assert.True(File.Exists(installer));
        Assert.Null(quarantine.Latest());
    }

    [Fact]
    public async Task RestoreNeverOverwritesANewerFile()
    {
        var installer = Make("Users/me/Downloads/setup.exe", 10, ageDays: 90);
        var finding = await ScanOne(Rule("installers", ["%USERPROFILE%\\Downloads"], files: "*.exe", age: 60, recurse: false, quarantine: true, level: CleanupLevel.Review));
        var quarantine = new CleanupQuarantine(Path.Combine(root, "undo"), () => Now);
        var result = await new CleanupCleaner(Context(), quarantine).CleanAsync([finding]);
        File.WriteAllText(installer, "newer download");
        var restored = quarantine.Restore(result.UndoId!);
        Assert.Equal(1, restored.Conflicts);
        Assert.Equal("newer download", File.ReadAllText(installer));
        Assert.NotNull(quarantine.Latest());
    }

    [Fact]
    public void ExpiredHoldsArePurgedAndRecentOnesKept()
    {
        var clock = Now;
        var quarantine = new CleanupQuarantine(Path.Combine(root, "undo"), () => clock);
        var batch = quarantine.Begin();
        var file = Make("Users/me/Downloads/x.exe", 5);
        batch.Hold("r", 0, Path.GetDirectoryName(file)!, file, 5);
        batch.Commit();
        clock = Now.AddDays(13);
        Assert.Equal(0, quarantine.Purge());
        clock = Now.AddDays(15);
        Assert.Equal(1, quarantine.Purge());
        Assert.Empty(quarantine.List());
    }

    [Fact]
    public async Task CancellingStopsAScanQuickly()
    {
        for (var i = 0; i < 200; i++) Make($"Users/me/AppData/Local/Temp/d{i % 10}/f{i}", 1);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CleanupScanner(Context()).ScanAsync([Rule("temp", ["%TEMP%"])], null, cancel.Token));
    }

    private static bool TryLink(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
