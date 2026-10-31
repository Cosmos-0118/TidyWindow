using System.Collections.Concurrent;
using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class OutputFormattingTests
{
    private const string Host = "<Obj S=\"information\"><ToString>openjdk27 installed successfully!</ToString><Props><S N=\"Source\">private-script-path.ps1</S><Obj N=\"Tags\"><LST><S>PSHOST</S></LST></Obj></Props></Obj>";
    private const string Progress = "<Obj S=\"progress\"><MS><PR><AV>Downloading Java</AV><PC>50</PC><T>Processing</T><SD>Getting archive</SD></PR></MS></Obj>";
    private const string Records = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" + Host + Progress + "<S S=\"warning\">Restart required_x000D__x000A_</S><S S=\"Error\">Actual error &amp; detail_x000A_</S></Objs>";

    [Fact]
    public void SerializedMessagesAreReadableAndKeepTheirRealTypes()
    {
        var values = new PowerShellOutputDecoder(CommandOutputKind.Error).Feed(Records, final: true);
        Assert.Equal(3, values.Count);
        Assert.Equal(CommandOutputKind.Progress, values[0].Kind); Assert.Contains("50%", values[0].Text);
        Assert.Equal(CommandOutputKind.Warning, values[1].Kind); Assert.Equal("Restart required\r\n", values[1].Text);
        Assert.Equal(CommandOutputKind.Error, values[2].Kind); Assert.Equal("Actual error & detail\n", values[2].Text);
        Assert.DoesNotContain(values, x => x.Text.Contains("PSHOST") || x.Text.Contains("private-script-path") || x.Text.Contains("<Obj"));
    }

    [Fact]
    public void EveryPossibleChunkBoundaryPreservesMessages()
    {
        var expected = new PowerShellOutputDecoder(CommandOutputKind.Error).Feed(Records, true);
        for (var boundary = 0; boundary <= Records.Length; boundary++)
        {
            var decoder = new PowerShellOutputDecoder(CommandOutputKind.Error);
            var actual = decoder.Feed(Records[..boundary]).Concat(decoder.Feed(Records[boundary..], true)).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ACompleteRecordStreamsBeforeTheDocumentCloses()
    {
        var decoder = new PowerShellOutputDecoder(CommandOutputKind.Error);
        var live = decoder.Feed("#< CLIXML\n<Objs><S S=\"warning\">Check this now</S>");
        Assert.Equal("Check this now\n", Assert.Single(live).Text);
        Assert.Empty(decoder.Feed("</Objs>", true));
    }

    [Fact]
    public void PlainStderrBeforeAndAfterSerializedRecordsKeepsStreaming()
    {
        var decoder = new PowerShellOutputDecoder(CommandOutputKind.Error);
        Assert.Equal("native error\n", Assert.Single(decoder.Feed("native error\n#< CL")).Text);
        var records = decoder.Feed("IXML\n<Objs><S S=\"warning\">A warning</S></Objs>another native error\n");
        Assert.Equal(CommandOutputKind.Warning, records[0].Kind);
        Assert.Equal("another native error\n", records[1].Text);
        Assert.Equal(CommandOutputKind.Error, records[1].Kind);
    }

    [Fact]
    public void ExtremelyVerboseOutputHasABoundedFormattedView()
    {
        var lines = CommandOutputFormatter.Format([new(string.Join("\n", Enumerable.Range(0, 5000).Select(i => "Line " + i)))]);
        Assert.Equal(601, lines.Count);
        Assert.Equal("Line 4999", lines[^1].Text);
        Assert.Contains("latest 600", lines[0].Text);
    }

    [Fact]
    public void PlainTextAndLiteralEscapeSequencesAreNotDamaged()
    {
        var decoder = new PowerShellOutputDecoder(CommandOutputKind.Output);
        Assert.Empty(decoder.Feed("#"));
        Assert.Equal("# ordinary output", Assert.Single(decoder.Feed(" ordinary output")).Text);
        Assert.Equal("next", Assert.Single(decoder.Feed("next", true)).Text);
        var escaped = new PowerShellOutputDecoder(CommandOutputKind.Error).Feed("#< CLIXML\n<Objs><S S=\"warning\">literal _x005F_x0041_</S></Objs>", true);
        Assert.Equal("literal _x0041_\n", Assert.Single(escaped).Text);
    }

    [Fact]
    public void IncompleteOversizedAndDtdRecordsNeverLeakXmlOrResolveEntities()
    {
        foreach (var input in new[]
        {
            "#< CLIXML\n<Objs><Obj S=\"information\"><S>incomplete",
            "#< CLIXML\n<Objs><S S=\"Error\">" + new string('x', 270000),
            "#< CLIXML\n<!DOCTYPE Obj [<!ENTITY secret SYSTEM 'file:///unreadable-secret'>]><Obj S=\"Error\">&secret;</Obj>"
        })
        {
            var output = new PowerShellOutputDecoder(CommandOutputKind.Error).Feed(input, true);
            Assert.Contains(output, x => x.Kind == CommandOutputKind.Warning);
            Assert.DoesNotContain(output, x => x.Text.Contains("<Obj") || x.Text.Contains("file://") || x.Text.Contains("&secret;"));
        }
    }

    [Theory]
    [InlineData("Downloading Java 50%", OutputTone.Progress)]
    [InlineData("Extracting archive", OutputTone.Progress)]
    [InlineData("Setting environment variable JAVA_HOME", OutputTone.Progress)]
    [InlineData("openjdk27 was installed successfully!", OutputTone.Success)]
    [InlineData("WARNING: Check configuration", OutputTone.Warning)]
    [InlineData("Restart required", OutputTone.Warning)]
    [InlineData("ERROR: Access denied", OutputTone.Error)]
    [InlineData("Download failed", OutputTone.Error)]
    [InlineData("0 errors; install failed", OutputTone.Error)]
    [InlineData("No errors detected", OutputTone.Normal)]
    [InlineData("https://example.com/archive.zip", OutputTone.Normal)]
    public void NormalMessagesHaveUsefulSemanticColors(string text, OutputTone expected)
        => Assert.Equal(expected, CommandOutputFormatter.Classify(text));

    [Fact]
    public void FormattingPreservesSplitLinesAndDoesNotMislabelSerializedInformationAsErrors()
    {
        var lines = CommandOutputFormatter.Format([
            new("Down"), new("loading file\r\n"), new("Package installed successfully\n", CommandOutputKind.Information),
            new("Needs attention\n", CommandOutputKind.Warning), new("real failure\n", CommandOutputKind.Error)]);
        Assert.Equal([OutputTone.Progress, OutputTone.Success, OutputTone.Warning, OutputTone.Error], lines.Select(x => x.Tone));
        Assert.Equal(["STEP", "OK", "WARN", "ERROR"], lines.Select(x => x.Label));
        Assert.Equal("Downloading file", lines[0].Text);
        Assert.DoesNotContain(lines, x => x.Text.Contains('\r'));
    }

    [Fact]
    public async Task ActualWindowsPowerShellWriteHostAndWarningAreNotDisplayedAsXmlErrors()
    {
        var script = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ProgressPreference='SilentlyContinue'; Write-Host 'installed successfully' -ForegroundColor Green; Write-Warning 'Check test warning'; [Console]::Error.WriteLine('real stderr error')";
        var outputs = new ConcurrentQueue<CommandOutput>();
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await new ProcessRunner().RunAsync(new CommandSpec(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(15))
        { Progress = new Callback(outputs.Enqueue) });
        Assert.Contains("installed successfully", result.Output);
        Assert.Contains("Check test warning", result.Error); Assert.Contains("real stderr error", result.Error);
        Assert.DoesNotContain("<Objs", result.Error); Assert.DoesNotContain("#< CLIXML", result.Error);
        Assert.Contains(outputs, x => x.Kind == CommandOutputKind.Warning && x.Text.Contains("Check test warning"));
        Assert.DoesNotContain(outputs, x => x.Kind == CommandOutputKind.Error && x.Text.Contains("PSHOST"));
    }
    private sealed class Callback(Action<CommandOutput> action) : IProgress<CommandOutput> { public void Report(CommandOutput value) => action(value); }
}
