using System.Text.Json;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ScoutBackendTests : IDisposable
{
    private readonly string host = Path.GetTempFileName();
    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]
    public void ServiceAndOtherSessionsDoNotCountAsTheUserEngine(int process, int current, bool expected)
        => Assert.Equal(expected, WindowsScoutEngine.IsUserProcess(process, current));

    [Fact]
    public async Task ServiceOnlyInstallationStartsTheUserEngineAndWaitsForIpc()
    {
        var engine = new FakeEngine(new("Everything.exe", false));
        var runner = new StatusRunner(new ScoutReply(false, [], Error: "Unavailable"), new(true, []));
        using var service = new ScoutService(runner, host, engine);
        var result = await service.CheckBackendAsync();
        Assert.True(result.Installed); Assert.True(result.Ready);
        Assert.Equal(1, engine.Starts); Assert.Equal(2, runner.Calls);
    }
    [Fact]
    public async Task InstalledButIndexingIsNotAnInstallRequestAndDoesNotStartAnotherProcess()
    {
        var engine = new FakeEngine(new("Everything.exe", true));
        using var service = new ScoutService(new StatusRunner(new ScoutReply(false, [], Error: "Indexing", Indexing: true)), host, engine);
        var result = await service.CheckBackendAsync();
        Assert.True(result.Installed); Assert.True(result.Indexing); Assert.False(result.Ready); Assert.Equal(0, engine.Starts);
    }
    [Fact]
    public async Task InstalledButUnavailableStaysInstalled()
    {
        var engine = new FakeEngine(new(null, true));
        using var service = new ScoutService(new StatusRunner(new ScoutReply(false, [], Error: "Unavailable")), host, engine);
        var result = await service.CheckBackendAsync();
        Assert.True(result.Installed); Assert.False(result.Ready); Assert.NotNull(result.Error); Assert.Equal(0, engine.Starts);
    }
    [Fact]
    public async Task MissingEngineIsNotStartedAndAnAlreadyReadyPortableEngineIsRecognized()
    {
        var engine = new FakeEngine(new(null, false));
        using var missing = new ScoutService(new StatusRunner(new ScoutReply(false, [])), host, engine);
        Assert.False((await missing.CheckBackendAsync()).Installed);
        using var portable = new ScoutService(new StatusRunner(new ScoutReply(true, [])), host, engine);
        Assert.True((await portable.CheckBackendAsync()).Installed);
        Assert.Equal(0, engine.Starts);
    }
    [Fact]
    public async Task StartupFailureIsReportedWithoutSuggestingAnotherInstall()
    {
        var engine = new FakeEngine(new("Everything.exe", false)) { FailStart = true };
        using var service = new ScoutService(new StatusRunner(new ScoutReply(false, [])), host, engine);
        var result = await service.CheckBackendAsync();
        Assert.True(result.Installed); Assert.False(result.Ready); Assert.Contains("could not start", result.Error);
    }
    public void Dispose() => File.Delete(host);
    private sealed class FakeEngine(ScoutEngineInstallation state) : IScoutEngine
    {
        public int Starts { get; private set; }
        public bool FailStart { get; init; }
        public ScoutEngineInstallation Inspect() => state;
        public void Start(string path) { Starts++; if (FailStart) throw new IOException("Fixture startup failure"); }
    }
    private sealed class StatusRunner(params ScoutReply[] replies) : ICommandRunner
    {
        public int Calls { get; private set; }
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        { var reply = replies[Math.Min(Calls++, replies.Length - 1)]; return Task.FromResult(new CommandResult(0, JsonSerializer.Serialize(reply), "")); }
    }
}
