using System.Globalization;
using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Tests;

public sealed class LabHardeningTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-labh-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PowercfgOutputCanBeDecodedUnderALocaleWithoutAnOemCodePage()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // Hindi has no OEM code page of its own (Windows reports 1), which used to throw before powercfg even started.
            CultureInfo.CurrentCulture = new CultureInfo("hi-IN");
            Assert.NotEqual(1, PowerCfgProcess.ConsoleEncoding().CodePage);
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void RoundsAndHowACheckWentSurviveTheFileAndAnOldFileStillLoads()
    {
        var store = new LabStore(folder);
        store.AddRun(new(DateTimeOffset.Now.AddMinutes(-5), "Balanced", "On", [new("wake", 2, 1)]));
        store.AddRun(new(DateTimeOffset.Now, "Balanced", "On", [new("wake", 2, 1, [1.9, 2.0, 2.1])], ProbeRunner.Method, 3.5, true));

        var runs = new LabStore(folder).Runs();
        Assert.Equal([1.9, 2.0, 2.1], runs[0].Metrics[0].Rounds); Assert.Equal(ProbeRunner.Method, runs[0].Method); Assert.Equal(3.5, runs[0].Busy); Assert.True(runs[0].OnBattery);
        Assert.Null(runs[1].Metrics[0].Rounds); Assert.Equal(0, runs[1].Method); Assert.Null(runs[1].Busy); Assert.Null(runs[1].OnBattery);
    }

    [Fact]
    public void ACheckOrNoteWithPartsMissingIsDroppedRatherThanCrashingThePage()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "checks.json"), """[{"When":"2026-01-01T00:00:00+00:00","Plan":null,"MemoryIntegrity":"On","Metrics":null},null,{"When":"2026-01-02T00:00:00+00:00","Plan":"Balanced","MemoryIntegrity":"On","Metrics":[{"Id":"wake","Value":2,"Spread":1}]}]""");
        File.WriteAllText(Path.Combine(folder, "power.json"), """{"PreviousId":null,"PreviousName":"Balanced","SwitchedToId":"b","SwitchedToName":"Ultimate","When":"2026-01-01T00:00:00+00:00"}""");
        var store = new LabStore(folder);

        Assert.Equal("Balanced", Assert.Single(store.Runs()).Plan);
        Assert.Null(store.GetPower());
        store.AddRun(new(DateTimeOffset.Now, "Balanced", "On", [new("wake", 2, 1)]));
        var runs = store.Runs();
        Assert.Equal(2, runs.Count);
        Assert.NotEmpty(ProbeCompare.Between(runs[1], runs[0]));
    }
}
