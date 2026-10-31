using System.Text.Json;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class StartupGuardTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-session-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset started = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly ActivityLog log = new();
    private readonly Dictionary<int, DateTimeOffset> running = [];
    public StartupGuardTests() => running[Environment.ProcessId] = started; // the test run itself is a live copy whose marker a second guard must leave alone
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private SessionGuard Guard(string? where = null) => new(where ?? folder, log, id => running.TryGetValue(id, out var at) ? at : null, () => started);
    private string Marker(int id) => Path.Combine(folder, id + ".json");
    private void Leave(int id, DateTimeOffset when, string content = "") =>
        File.WriteAllText(Marker(id), content.Length > 0 ? content : JsonSerializer.Serialize(new EndedSession(id, when, @"C:\TidyWindow\TidyWindow.Next.exe")));

    [Fact]
    public void ACopyKeepsAMarkerWhileRunningAndRemovesItWhenItCloses()
    {
        var guard = Guard();
        guard.Begin();
        Assert.True(File.Exists(Marker(Environment.ProcessId)));
        guard.End();
        Assert.False(File.Exists(Marker(Environment.ProcessId)));
    }

    [Fact]
    public void ACopyThatEndedWithoutCleaningUpIsReportedOnceAndForgotten()
    {
        Directory.CreateDirectory(folder);
        Leave(4242, started);
        var ended = Assert.Single(Guard().Begin());
        Assert.Equal(4242, ended.ProcessId);
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Equal("TidyWindow closed unexpectedly last time", entry.Message);
        Assert.Contains("Event Viewer", entry.Detail);
        Assert.False(File.Exists(Marker(4242)));

        Assert.Empty(Guard().Begin());
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void ACopyThatIsStillRunningIsNotReported()
    {
        Directory.CreateDirectory(folder);
        Leave(4242, started);
        running[4242] = started + TimeSpan.FromSeconds(1);
        Assert.Empty(Guard().Begin());
        Assert.Empty(log.Snapshot());
        Assert.True(File.Exists(Marker(4242)));
    }

    [Fact]
    public void AProcessIdReusedByAnotherProgramStillCountsAsACrash()
    {
        Directory.CreateDirectory(folder);
        Leave(4242, started);
        running[4242] = started + TimeSpan.FromHours(3);
        Assert.Single(Guard().Begin());
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void AMarkerThatCannotBeReadIsDroppedSilently()
    {
        Directory.CreateDirectory(folder);
        Leave(4242, started, "{ not json");
        Assert.Empty(Guard().Begin());
        Assert.Empty(log.Snapshot());
        Assert.False(File.Exists(Marker(4242)));
    }

    [Fact]
    public void AFolderThatCannotBeUsedNeverThrows()
    {
        // A file where the folder should be: creating the folder and writing a marker both fail.
        Directory.CreateDirectory(folder);
        var blocked = Path.Combine(folder, "blocked");
        File.WriteAllText(blocked, "");
        var guard = Guard(Path.Combine(blocked, "sessions"));
        Assert.Empty(guard.Begin());
        guard.End();
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void TheEntryPointKeepsItsSingleThreadedApartment()
    {
        // Without the attribute the app starts, then dies within seconds with a native error that nothing can log, and only some of the time.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TidyWindow.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "TidyWindow.Desktop", "Program.cs"));
        Assert.Matches(@"\[STAThread\]\s+(?:(?:private|public|internal)\s+)?static\s+void\s+Main\b", source);
    }
}
