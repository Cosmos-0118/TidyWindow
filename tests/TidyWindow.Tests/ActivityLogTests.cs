using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ActivityLogTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-log-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private ActivityLog Create(string? where = null, int capacity = 500) => new(where, capacity, () => now);
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void RepeatedEventUpdatesOneEntryInsteadOfFloodingTheLog()
    {
        var log = Create();
        log.Write(ActivityLevel.Warning, ActivityArea.Search, "WinGet search failed", "first reason");
        now += TimeSpan.FromMinutes(5);
        log.Write(ActivityLevel.Warning, ActivityArea.Search, "WinGet search failed", "second reason");
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(2, entry.Count);
        Assert.Equal("second reason", entry.Detail);
        Assert.Equal(now, entry.Time);
    }

    [Fact]
    public void RepeatMovesToTheEndSoOrderStaysChronological()
    {
        var log = Create();
        log.Write(ActivityLevel.Info, ActivityArea.Install, "Installing Git");
        log.Write(ActivityLevel.Error, ActivityArea.Install, "Couldn’t install Git");
        log.Write(ActivityLevel.Info, ActivityArea.Install, "Installing Git");
        Assert.Equal(["Couldn’t install Git", "Installing Git"], log.Snapshot().Select(x => x.Message));
    }

    [Fact]
    public void SameMessageAtDifferentSeverityOrAfterTheWindowIsANewEntry()
    {
        var log = Create();
        log.Write(ActivityLevel.Warning, ActivityArea.Managers, "Scoop needs repair");
        log.Write(ActivityLevel.Error, ActivityArea.Managers, "Scoop needs repair");
        now += TimeSpan.FromHours(7);
        log.Write(ActivityLevel.Error, ActivityArea.Managers, "Scoop needs repair");
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void MemoryIsBoundedToCapacity()
    {
        var log = Create(capacity: 3);
        for (var i = 0; i < 10; i++) log.Write(ActivityLevel.Info, ActivityArea.App, "event " + i);
        Assert.Equal(["event 7", "event 8", "event 9"], log.Snapshot().Select(x => x.Message));
    }

    [Fact]
    public void EntriesSurviveRestartIncludingRepeatCounts()
    {
        var first = Create(folder);
        first.Write(ActivityLevel.Error, ActivityArea.Install, "Couldn’t install Git", "Exit code: 1");
        first.Write(ActivityLevel.Error, ActivityArea.Install, "Couldn’t install Git", "Exit code: 2");
        first.Write(ActivityLevel.Success, ActivityArea.Managers, "WinGet is working again");
        var restored = Create(folder).Snapshot();
        Assert.Equal(["Couldn’t install Git", "WinGet is working again"], restored.Select(x => x.Message));
        Assert.Equal(2, restored[0].Count);
        Assert.Equal("Exit code: 2", restored[0].Detail);
        Assert.Equal(ActivityLevel.Error, restored[0].Level);
        Assert.Equal(ActivityArea.Install, restored[0].Area);
    }

    [Fact]
    public void DamagedLinesAreSkippedAndGoodEntriesStillLoad()
    {
        Create(folder).Write(ActivityLevel.Info, ActivityArea.App, "kept");
        File.AppendAllText(Path.Combine(folder, "activity.jsonl"), "{ not json\n{\"id\":\"x\"}\n\n{\"Id\":\"y\",\"Level\":\"Nonsense\"}\n");
        Assert.Equal("kept", Assert.Single(Create(folder).Snapshot()).Message);
    }

    [Fact]
    public void LargeFileRotatesAndBothGenerationsReload()
    {
        var log = Create(folder);
        var filler = new string('x', 3000);
        for (var i = 0; i < 400; i++) log.Write(ActivityLevel.Info, ActivityArea.App, "entry " + i, filler + i);
        Assert.True(File.Exists(Path.Combine(folder, "activity.previous.jsonl")));
        var restored = Create(folder).Snapshot();
        Assert.Equal("entry 399", restored[^1].Message);
        Assert.Equal(400, restored.Count);
    }

    [Fact]
    public void ClearRemovesMemoryAndFilesAndNotifies()
    {
        var log = Create(folder);
        var cleared = 0; log.Cleared += () => cleared++;
        log.Write(ActivityLevel.Info, ActivityArea.App, "one");
        log.Clear();
        Assert.Equal(0, log.Count); Assert.Equal(1, cleared);
        Assert.Empty(Create(folder).Snapshot());
    }

    [Fact]
    public void UnwritableFolderIsReportedAndNeverThrows()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "tidywindow-block-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file, not a folder");
        try
        {
            var log = Create(Path.Combine(blocker, "Logs"));
            log.Write(ActivityLevel.Error, ActivityArea.App, "still recorded in memory");
            Assert.NotNull(log.StorageError);
            Assert.Single(log.Snapshot());
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void FailingSubscriberCannotBreakTheWriter()
    {
        var log = Create();
        var seen = 0;
        log.Written += _ => throw new InvalidOperationException();
        log.Written += _ => seen++;
        log.Write(ActivityLevel.Info, ActivityArea.App, "hello");
        Assert.Equal(1, seen);
    }

    [Fact]
    public void TextIsBoundedCleanedAndFreeOfTheAccountName()
    {
        var log = Create();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var entry = log.Write(ActivityLevel.Error, ActivityArea.App, "line one\r\nline two\u0007 " + new string('m', 500), $"failed at {profile}\\tool.exe\n" + new string('d', 9000));
        Assert.DoesNotContain('\n', entry.Message);
        Assert.DoesNotContain('\u0007', entry.Message);
        Assert.True(entry.Message.Length <= 200);
        Assert.Contains("%USERPROFILE%", entry.Detail);
        Assert.DoesNotContain(profile, entry.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(entry.Detail!.Length <= 4010);
        Assert.StartsWith("failed at", entry.Detail);
    }

    [Fact]
    public void BlankMessageIsNeverStoredEmpty()
    {
        Assert.Equal("(no message)", Create().Write(ActivityLevel.Info, ActivityArea.App, "  ", "   ").Message);
        Assert.Null(Create().Write(ActivityLevel.Info, ActivityArea.App, "x", "   ").Detail);
    }

    [Fact]
    public void FilterCombinesSeverityAreaAndTextAcrossMessageAndDetail()
    {
        var log = Create();
        var install = log.Write(ActivityLevel.Error, ActivityArea.Install, "Couldn’t install Git", "Exit code: 1603");
        var search = log.Write(ActivityLevel.Warning, ActivityArea.Search, "Scoop search failed");
        var info = log.Write(ActivityLevel.Info, ActivityArea.Install, "Installing Git");
        Assert.True(new ActivityFilter().Matches(info));
        Assert.False(new ActivityFilter(ActivityLevel.Warning).Matches(info));
        Assert.True(new ActivityFilter(ActivityLevel.Warning).Matches(search));
        Assert.False(new ActivityFilter(ActivityLevel.Error).Matches(search));
        Assert.False(new ActivityFilter(Area: ActivityArea.Search).Matches(install));
        Assert.True(new ActivityFilter(Text: " 1603 ").Matches(install));
        Assert.True(new ActivityFilter(Text: "GIT").Matches(install));
        Assert.False(new ActivityFilter(Text: "1603").Matches(info));
    }

    [Fact]
    public void TranscriptIsReadableAndIndentsDetails()
    {
        var log = Create();
        log.Write(ActivityLevel.Error, ActivityArea.Install, "Couldn’t install Git", "Exit code: 1\nAccess denied");
        log.Write(ActivityLevel.Error, ActivityArea.Install, "Couldn’t install Git");
        var text = ActivityLog.Format(log.Snapshot());
        Assert.Contains("ERROR", text); Assert.Contains("Install", text); Assert.Contains("(x2)", text);
        Assert.Contains("    Exit code: 1", text); Assert.Contains("    Access denied", text);
    }

    [Fact]
    public async Task ConcurrentWritersLoseNothingAndKeepTheFileReadable()
    {
        var log = Create(folder);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() => { for (var i = 0; i < 50; i++) log.Write(ActivityLevel.Info, ActivityArea.App, $"writer {t} event {i}"); })));
        Assert.Equal(400, log.Count);
        Assert.Equal(400, Create(folder).Count);
    }
}

public sealed class ManagerStatusLogTests
{
    private static ManagerStatus Status(ManagerHealth health, ManagerProblem problem = ManagerProblem.None, string version = "") =>
        new(ManagerDefinition.All[1], health, version, health == ManagerHealth.Missing ? null : @"C:\choco.exe", "detail text", problem);

    [Fact]
    public void FirstLookAtAHealthyOrAbsentManagerIsSilent()
    {
        var log = new ActivityLog();
        ManagerStatusLog.Record(log, null, Status(ManagerHealth.Ready, version: "2.0"));
        ManagerStatusLog.Record(log, null, Status(ManagerHealth.Missing, ManagerProblem.NotInstalled));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void UnchangedStatusIsSilentEvenWhenBroken()
    {
        var log = new ActivityLog();
        var broken = Status(ManagerHealth.Unavailable, ManagerProblem.Configuration);
        ManagerStatusLog.Record(log, null, broken);
        ManagerStatusLog.Record(log, broken, broken with { Detail = "different wording" });
        Assert.Equal(1, log.Count);
        Assert.Equal(1, log.Snapshot()[0].Count);
    }

    [Fact]
    public void ProblemsAndRecoveriesAreRecordedOnTransition()
    {
        var log = new ActivityLog();
        var ready = Status(ManagerHealth.Ready, version: "2.0");
        ManagerStatusLog.Record(log, ready, Status(ManagerHealth.ConnectionIssue, ManagerProblem.Network));
        ManagerStatusLog.Record(log, Status(ManagerHealth.ConnectionIssue, ManagerProblem.Network), ready);
        ManagerStatusLog.Record(log, ready, Status(ManagerHealth.Missing, ManagerProblem.NotInstalled));
        ManagerStatusLog.Record(log, Status(ManagerHealth.Missing, ManagerProblem.NotInstalled), ready);
        var entries = log.Snapshot();
        Assert.Equal(["Chocolatey can't reach its package source", "Chocolatey is working again", "Chocolatey was not found", "Chocolatey is now installed"], entries.Select(x => x.Message));
        Assert.Equal([ActivityLevel.Warning, ActivityLevel.Success, ActivityLevel.Warning, ActivityLevel.Success], entries.Select(x => x.Level));
        Assert.Equal("Version 2.0", entries[1].Detail);
    }
}
