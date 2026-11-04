using System.Diagnostics;
using System.Runtime.InteropServices;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The fixes from the security and robustness review: what each one must keep true.</summary>
public sealed class CleanupHardeningTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-harden-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, string> variables;

    public CleanupHardeningTests()
    {
        var profile = Path.Combine(root, "Users", "me");
        variables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = profile, ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local"), ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
            ["TEMP"] = Path.Combine(profile, "AppData", "Local", "Temp"), ["WINDIR"] = Path.Combine(root, "Windows"), ["PROGRAMDATA"] = Path.Combine(root, "ProgramData"), ["SYSTEMDRIVE"] = root
        };
        Directory.CreateDirectory(root);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private CleanupContext Context(Func<DateTime>? now = null, int parallelism = 4) => new()
    {
        Variable = name => variables.GetValueOrDefault(name), IsAdministrator = false, Now = now ?? (() => Now), RunningProcesses = () => new HashSet<string>(), Parallelism = parallelism
    };

    private string Make(string relative, int bytes = 100, int ageDays = 0)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays));
        return path;
    }

    private static CleanupRule Rule(string id, string[] paths, string? files = null, int age = 0, bool recurse = true, bool quarantine = false, CleanupLevel level = CleanupLevel.Recommended) =>
        new() { Id = id, Group = "Test", Name = id, Detail = "d", Paths = paths, Files = files, MinAgeDays = age, Recurse = recurse, Quarantine = quarantine, Level = level };

    private async Task<CleanupFinding> ScanOne(CleanupRule rule) => (await new CleanupScanner(Context()).ScanAsync([rule])).Single();

    [Fact]
    public void TheRecycleBinStructIsLaidOutAsTheShellDeclaresIt()
    {
        // SHQUERYRBINFO is a DWORD followed by two 64-bit values with natural alignment: 24 bytes, the sizes at offsets 8 and 16. Packed, it was 20 bytes and the shell wrote past its end.
        if (!Environment.Is64BitProcess) return;
        Assert.Equal(24, Marshal.SizeOf<CleanupNative.RecycleBinInfo>());
        Assert.Equal(8, Marshal.OffsetOf<CleanupNative.RecycleBinInfo>(nameof(CleanupNative.RecycleBinInfo.Bytes)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<CleanupNative.RecycleBinInfo>(nameof(CleanupNative.RecycleBinInfo.Items)).ToInt32());
    }

    [Fact]
    public async Task AnUndoFolderThatCannotHoldFilesIsAFailureNotAFileInUse()
    {
        var installer = Make("Users/me/Downloads/setup.exe", 10, ageDays: 90);
        // A file sits where the undo folder should be, so nothing can be moved into it.
        var blocked = Path.Combine(root, "undo");
        File.WriteAllText(blocked, "not a folder");
        var finding = await ScanOne(Rule("installers", ["%USERPROFILE%\\Downloads"], files: "*.exe", age: 60, recurse: false, quarantine: true, level: CleanupLevel.Review));

        var result = await new CleanupCleaner(Context(), new CleanupQuarantine(blocked, () => Now)).CleanAsync([finding]);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.InUse);
        Assert.Null(result.UndoId);
        Assert.True(File.Exists(installer));
    }

    [Fact]
    public async Task ACleanThatIsStoppedStillReportsWhatItRemoved()
    {
        var temp = Path.Combine(root, "Users", "me", "AppData", "Local", "Temp");
        for (var i = 0; i < 20; i++) Make($"Users/me/AppData/Local/Temp/top{i}.tmp", 1, ageDays: 5);
        // Enough files in the subfolder for the walk to look at the stop request while it is still listing them.
        for (var i = 0; i < 1100; i++) Make($"Users/me/AppData/Local/Temp/deep/f{i}.tmp", 1, ageDays: 5);
        var finding = await ScanOne(Rule("temp", ["%TEMP%"], age: 1));
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        // The clock is read once before the sweep and once per folder swept (the folder itself first, then "deep"): the stop arrives as "deep" begins.
        var context = Context(now: () => { if (++calls == 3) cancel.Cancel(); return Now; }, parallelism: 1);

        var result = await new CleanupCleaner(context).CleanAsync([finding], null, cancel.Token);

        Assert.True(result.Stopped);
        Assert.Equal(20, result.Removed);
        Assert.Equal(1100, Directory.EnumerateFiles(Path.Combine(temp, "deep")).Count());
        Assert.Empty(Directory.EnumerateFiles(temp));
    }

    [Fact]
    public async Task ATopLevelFolderSwappedForALinkDuringTheCleanIsNotWalked()
    {
        var outside = Path.Combine(root, "outside");
        Make("outside/precious.doc", 777, ageDays: 5);
        var temp = Path.Combine(root, "Users", "me", "AppData", "Local", "Temp");
        var cache = Path.Combine(temp, "cache");
        Make("Users/me/AppData/Local/Temp/cache/old.tmp", 10, ageDays: 5);
        var finding = await ScanOne(Rule("temp", ["%TEMP%"], age: 1));
        var calls = 0; var swapped = false;
        // The second reading of the clock comes from the sweep of the folder itself: after its subfolders were listed, before any of them is walked.
        var context = Context(now: () =>
        {
            if (++calls == 2) { Directory.Delete(cache, true); swapped = TryLink(cache, outside); }
            return Now;
        }, parallelism: 1);

        var result = await new CleanupCleaner(context).CleanAsync([finding]);

        if (!swapped) return;
        Assert.True(File.Exists(Path.Combine(outside, "precious.doc")));
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void ABatchWhoseHeldFilesAreGoneIsNotOfferedForUndo()
    {
        var quarantine = new CleanupQuarantine(Path.Combine(root, "undo"), () => Now);
        var batch = quarantine.Begin();
        var file = Make("Users/me/Downloads/x.exe", 5);
        batch.Hold("r", 0, Path.GetDirectoryName(file)!, file, 5);
        batch.Commit();
        Assert.NotNull(quarantine.Latest());

        File.Delete(Directory.EnumerateFiles(Path.Combine(root, "undo"), "x.exe", SearchOption.AllDirectories).Single());

        Assert.Null(quarantine.Latest());
        Assert.Equal(new RestoreResult(0, 0, 1), quarantine.Restore(batch.Id));
    }

    [Fact]
    public void AnUndoFolderThatIsNotAFolderHasNothingToOfferAndThrowsNothing()
    {
        var file = Path.Combine(root, "undo-as-file");
        File.WriteAllText(file, "x");
        var quarantine = new CleanupQuarantine(file, () => Now);
        Assert.Empty(quarantine.List());
        Assert.Null(quarantine.Latest());
        Assert.Equal(0, quarantine.Purge());
        Assert.Equal(new RestoreResult(0, 0, 0), quarantine.Restore("nothing"));
    }

    [Fact]
    public void TheShippedCleanupCatalogPassesItsOwnRules() => Assert.Empty(CleanupGuard.Validate(CleanupCatalog.Load()));

    [Fact]
    public void ABrokenCleanupRuleIsReported()
    {
        static CleanupRule Good(string id = "a-b") => new() { Id = id, Group = "g", Name = "n", Detail = "d", Paths = ["%TEMP%\\x"] };
        Assert.Empty(CleanupGuard.Validate([Good()]));
        Assert.NotEmpty(CleanupGuard.Validate([Good(), Good()]));
        Assert.NotEmpty(CleanupGuard.Validate([Good("Bad Id")]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "", Paths = ["%TEMP%\\x"] }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Paths = ["C:\\Temp"] }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Paths = ["%TEMP%\\..\\x"] }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Paths = [] }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Action = CleanupAction.Hibernation, Level = CleanupLevel.Review }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Paths = ["%TEMP%"], Quarantine = true }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Handlers = ["a\\b"] }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Paths = ["%TEMP%"], Files = "..\\x.exe" }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Action = CleanupAction.DiskCleanup, Admin = true, Level = CleanupLevel.Review }]));
        Assert.NotEmpty(CleanupGuard.Validate([new() { Id = "a", Group = "g", Name = "n", Detail = "d", Paths = ["%TEMP%"], Discover = "everything" }]));
    }

    [Fact]
    public void WindowsLogsAreCleanedBySubfolderNeverAsWholeLogRoots()
    {
        var rule = CleanupCatalog.Load().Single(x => x.Id == "servicing-logs");
        Assert.DoesNotContain("%WINDIR%\\Logs", rule.Paths);
        Assert.DoesNotContain("%WINDIR%\\System32\\LogFiles", rule.Paths);
        Assert.All(rule.Paths.Where(x => x.StartsWith("%WINDIR%")), path => Assert.True(path.Count(c => c == '\\') >= 2, path));
        Assert.Equal(7, rule.MinAgeDays);
    }

    private static bool TryLink(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
