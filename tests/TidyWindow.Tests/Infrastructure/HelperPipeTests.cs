using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class HelperPipeTests
{
    private static PipeLineReader Reader(string text, int max = HelperPipe.MaxLineLength) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), max);

    [Fact]
    public async Task LinesAreSplitOnNewlinesWhateverTheChunking()
    {
        var reader = new PipeLineReader(new TrickleStream(Encoding.UTF8.GetBytes("first\r\nsecond\nthird")));
        Assert.Equal("first", await reader.ReadLineAsync());
        Assert.Equal("second", await reader.ReadLineAsync());
        Assert.Equal("third", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public void TheSynchronousReaderBehavesTheSameWay()
    {
        var reader = Reader("a\n\nb\n");
        Assert.Equal("a", reader.ReadLine());
        Assert.Equal("", reader.ReadLine());
        Assert.Equal("b", reader.ReadLine());
        Assert.Null(reader.ReadLine());
    }

    [Fact]
    public async Task MultiByteCharactersSplitAcrossChunksAreDecodedWhole()
    {
        var reader = new PipeLineReader(new TrickleStream(Encoding.UTF8.GetBytes("漢字🙂 ok\n")));
        Assert.Equal("漢字🙂 ok", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task ALineBeyondTheCeilingEndsTheConversationInsteadOfBeingBuffered()
    {
        var reader = Reader(new string('x', 100) + "\nshort\n", max: 50);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadLineAsync());
    }

    [Fact]
    public async Task TheAppsPipeAcceptsTheHelperAndCarriesLinesBothWays()
    {
        var name = HelperPipe.NewName("test");
        Assert.StartsWith("tidywindow-test-", name);
        var step = TimeSpan.FromSeconds(5);
        await using var server = HelperPipe.CreateServer(name);
        var waiting = server.WaitForConnectionAsync();
        await using var client = await HelperPipe.ConnectAsync(name).WaitAsync(step);
        await waiting.WaitAsync(step);

        // Flushing a pipe waits until the other side has read, so each read is started before the matching write, as the hosts and sessions do.
        var toHelper = new StreamWriter(server) { AutoFlush = true };
        var fromApp = new PipeLineReader(client);
        var helperReads = fromApp.ReadLineAsync();
        await toHelper.WriteLineAsync("{\"op\":\"hello\"}").WaitAsync(step);
        Assert.Equal("{\"op\":\"hello\"}", await helperReads.WaitAsync(step));

        var toApp = new StreamWriter(client) { AutoFlush = true };
        var fromHelper = new PipeLineReader(server);
        var appReads = fromHelper.ReadLineAsync();
        await toApp.WriteLineAsync("{\"ok\":true}").WaitAsync(step);
        Assert.Equal("{\"ok\":true}", await appReads.WaitAsync(step));
    }

    [Fact]
    public async Task APipeNobodyMadeIsNotWaitedForForever()
    {
        Assert.Null(await HelperPipe.TryConnectAsync(HelperPipe.NewName("nobody"), timeout: 200));
    }

    [Fact]
    public async Task ADeclinedPromptIsRecognisedHoweverTheLauncherReportedIt()
    {
        var thrown = Task.Run(() => throw new OperationCanceledException("declined"));
        try { await thrown; } catch (OperationCanceledException) { }
        Assert.True(HelperPipe.WasDeclined(thrown));
        Assert.True(HelperPipe.WasDeclined(Task.FromCanceled(new CancellationToken(true))));
        Assert.True(HelperPipe.WasDeclined(Task.FromException(new OperationCanceledException())));
        Assert.False(HelperPipe.WasDeclined(Task.FromException(new IOException("broken"))));
        Assert.False(HelperPipe.WasDeclined(Task.CompletedTask));
    }

    [Fact]
    public void AMissingHelperIsAnIoProblemTheAppCanShow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "tw-missing-" + Guid.NewGuid().ToString("N"), "TidyWindow.CleanHost.exe");
        // The check happens before any task is made, so the failure is immediate rather than a faulted task.
        var ex = Assert.Throws<FileNotFoundException>(() => { _ = HelperPipe.LaunchElevated(missing, "--tune", "pipe"); });
        Assert.IsAssignableFrom<IOException>(ex);
        Assert.Contains("missing", ex.Message);
    }

    /// <summary>A stream that hands out one byte per read, so every multi-byte character and every line ending is split across reads.</summary>
    private sealed class TrickleStream(byte[] bytes) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= bytes.Length || count == 0) return 0;
            buffer[offset] = bytes[position++];
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
