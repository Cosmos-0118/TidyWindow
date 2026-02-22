using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class ProcessControlServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _startTypesPath;

    public ProcessControlServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TidyWindow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _startTypesPath = Path.Combine(_tempDir, "service-original-starttypes.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private ProcessControlService CreateService() => new(_startTypesPath);

    private ProcessControlService CreateServiceWithPreExistingData(Dictionary<string, string> data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_startTypesPath, json);
        return new ProcessControlService(_startTypesPath);
    }

    // ── MapStartModeToScString ──────────────────────────────────────────

    [Theory]
    [InlineData(ServiceStartMode.Automatic, "auto")]
    [InlineData(ServiceStartMode.Manual, "demand")]
    [InlineData(ServiceStartMode.Boot, "boot")]
    [InlineData(ServiceStartMode.System, "system")]
    public void MapStartModeToScString_ReturnsCorrectScValue(ServiceStartMode mode, string expected)
    {
        var actual = ProcessControlService.MapStartModeToScString(mode);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MapStartModeToScString_DisabledFallsBackToDemand()
    {
        var actual = ProcessControlService.MapStartModeToScString(ServiceStartMode.Disabled);
        Assert.Equal("demand", actual);
    }

    [Fact]
    public void MapStartModeToScString_UnknownValueFallsBackToDemand()
    {
        // Cast to an out-of-range value to simulate unknown enum.
        var actual = ProcessControlService.MapStartModeToScString((ServiceStartMode)999);
        Assert.Equal("demand", actual);
    }

    // ── SaveOriginalStartType ───────────────────────────────────────────

    [Fact]
    public void SaveOriginalStartType_StoresAutomaticAsAuto()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Automatic);

        Assert.Equal("auto", svc.GetOriginalStartType("TestSvc"));
    }

    [Fact]
    public void SaveOriginalStartType_StoresManualAsDemand()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Manual);

        Assert.Equal("demand", svc.GetOriginalStartType("TestSvc"));
    }

    [Fact]
    public void SaveOriginalStartType_DoesNotOverwriteExistingEntry()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Automatic);
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Manual);

        // Should still be "auto" from the first save — never overwrite.
        Assert.Equal("auto", svc.GetOriginalStartType("TestSvc"));
    }

    [Fact]
    public void SaveOriginalStartType_IsCaseInsensitive()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Automatic);

        Assert.Equal("auto", svc.GetOriginalStartType("TESTSVC"));
        Assert.Equal("auto", svc.GetOriginalStartType("testsvc"));
    }

    [Fact]
    public void SaveOriginalStartType_TrimsWhitespace()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("  TestSvc  ", ServiceStartMode.Automatic);

        Assert.Equal("auto", svc.GetOriginalStartType("TestSvc"));
    }

    // ── GetOriginalStartType ────────────────────────────────────────────

    [Fact]
    public void GetOriginalStartType_ReturnsDefaultDemandWhenUnknown()
    {
        var svc = CreateService();
        Assert.Equal("demand", svc.GetOriginalStartType("NeverSavedService"));
    }

    [Fact]
    public void GetOriginalStartType_ReturnsSavedValue()
    {
        var svc = CreateServiceWithPreExistingData(new Dictionary<string, string>
        {
            ["WSearch"] = "delayed-auto",
            ["Spooler"] = "auto"
        });

        Assert.Equal("delayed-auto", svc.GetOriginalStartType("WSearch"));
        Assert.Equal("auto", svc.GetOriginalStartType("Spooler"));
    }

    // ── RemoveOriginalStartType ─────────────────────────────────────────

    [Fact]
    public void RemoveOriginalStartType_ClearsEntry()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Automatic);

        svc.RemoveOriginalStartType("TestSvc");

        // Should fall back to default "demand".
        Assert.Equal("demand", svc.GetOriginalStartType("TestSvc"));
    }

    [Fact]
    public void RemoveOriginalStartType_NoOpsForUnknownService()
    {
        var svc = CreateService();

        // Should not throw.
        svc.RemoveOriginalStartType("NeverSaved");
    }

    [Fact]
    public void RemoveOriginalStartType_AllowsReRecordingAfterRemoval()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Automatic);
        svc.RemoveOriginalStartType("TestSvc");

        // Now saving again should work (not blocked by "already saved").
        svc.SaveOriginalStartType("TestSvc", ServiceStartMode.Manual);
        Assert.Equal("demand", svc.GetOriginalStartType("TestSvc"));
    }

    // ── Persistence (survives recreation) ───────────────────────────────

    [Fact]
    public void Persistence_SavedEntriesSurviveServiceRecreation()
    {
        var svc1 = CreateService();
        svc1.SaveOriginalStartType("Spooler", ServiceStartMode.Automatic);
        svc1.SaveOriginalStartType("WSearch", ServiceStartMode.Manual);

        // Create a new service pointing at the same file — simulates app restart.
        var svc2 = new ProcessControlService(_startTypesPath);

        Assert.Equal("auto", svc2.GetOriginalStartType("Spooler"));
        Assert.Equal("demand", svc2.GetOriginalStartType("WSearch"));
    }

    [Fact]
    public void Persistence_RemovalIsPersisted()
    {
        var svc1 = CreateService();
        svc1.SaveOriginalStartType("Spooler", ServiceStartMode.Automatic);
        svc1.SaveOriginalStartType("WSearch", ServiceStartMode.Manual);
        svc1.RemoveOriginalStartType("Spooler");

        var svc2 = new ProcessControlService(_startTypesPath);

        Assert.Equal("demand", svc2.GetOriginalStartType("Spooler")); // was removed → default
        Assert.Equal("demand", svc2.GetOriginalStartType("WSearch")); // was saved
    }

    [Fact]
    public void Persistence_CorruptedFileDoesNotThrow()
    {
        File.WriteAllText(_startTypesPath, "THIS IS NOT JSON {{{{");

        var svc = new ProcessControlService(_startTypesPath);

        // Should fall back to empty and not throw.
        Assert.Equal("demand", svc.GetOriginalStartType("Anything"));
    }

    [Fact]
    public void Persistence_EmptyFileDoesNotThrow()
    {
        File.WriteAllText(_startTypesPath, "");

        var svc = new ProcessControlService(_startTypesPath);
        Assert.Equal("demand", svc.GetOriginalStartType("Anything"));
    }

    [Fact]
    public void Persistence_MissingFileDoesNotThrow()
    {
        // _startTypesPath doesn't exist yet — should not throw.
        var svc = new ProcessControlService(_startTypesPath);
        Assert.Equal("demand", svc.GetOriginalStartType("Anything"));
    }

    [Fact]
    public void Persistence_FileIsValidJson()
    {
        var svc = CreateService();
        svc.SaveOriginalStartType("Spooler", ServiceStartMode.Automatic);

        Assert.True(File.Exists(_startTypesPath));

        var json = File.ReadAllText(_startTypesPath);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        Assert.NotNull(dict);
        Assert.True(dict!.ContainsKey("Spooler"));
        Assert.Equal("auto", dict["Spooler"]);
    }

    // ── Full disable → re-enable flow (unit-level) ──────────────────────

    [Fact]
    public void FullFlow_SaveThenRestoreThenEntryIsGone()
    {
        var svc = CreateService();

        // 1. Service was Automatic — we're about to disable it.
        svc.SaveOriginalStartType("GamingServices", ServiceStartMode.Automatic);
        Assert.Equal("auto", svc.GetOriginalStartType("GamingServices"));

        // 2. User switches to Keep — we look up the original.
        var restored = svc.GetOriginalStartType("GamingServices");
        Assert.Equal("auto", restored);

        // 3. After successful restore, clean up.
        svc.RemoveOriginalStartType("GamingServices");
        Assert.Equal("demand", svc.GetOriginalStartType("GamingServices"));
    }

    [Fact]
    public void FullFlow_ManualServiceIsRestoredAsManual()
    {
        var svc = CreateService();

        svc.SaveOriginalStartType("SomeManualSvc", ServiceStartMode.Manual);
        Assert.Equal("demand", svc.GetOriginalStartType("SomeManualSvc"));

        svc.RemoveOriginalStartType("SomeManualSvc");
    }

    [Fact]
    public void FullFlow_MultipleServicesTrackedIndependently()
    {
        var svc = CreateService();

        svc.SaveOriginalStartType("Svc_Auto", ServiceStartMode.Automatic);
        svc.SaveOriginalStartType("Svc_Manual", ServiceStartMode.Manual);

        Assert.Equal("auto", svc.GetOriginalStartType("Svc_Auto"));
        Assert.Equal("demand", svc.GetOriginalStartType("Svc_Manual"));

        // Remove only one.
        svc.RemoveOriginalStartType("Svc_Auto");

        Assert.Equal("demand", svc.GetOriginalStartType("Svc_Auto")); // cleared → default
        Assert.Equal("demand", svc.GetOriginalStartType("Svc_Manual")); // still saved
    }

    [Fact]
    public void FullFlow_PreExistingDataRoundtripsCorrectly()
    {
        var svc1 = CreateServiceWithPreExistingData(new Dictionary<string, string>
        {
            ["GameBar"] = "delayed-auto",
            ["XboxNetApiSvc"] = "auto",
            ["SomeManual"] = "demand"
        });

        Assert.Equal("delayed-auto", svc1.GetOriginalStartType("GameBar"));
        Assert.Equal("auto", svc1.GetOriginalStartType("XboxNetApiSvc"));
        Assert.Equal("demand", svc1.GetOriginalStartType("SomeManual"));

        // Remove one, add one, then simulate restart.
        svc1.RemoveOriginalStartType("XboxNetApiSvc");
        svc1.SaveOriginalStartType("NewSvc", ServiceStartMode.Automatic);

        var svc2 = new ProcessControlService(_startTypesPath);
        Assert.Equal("delayed-auto", svc2.GetOriginalStartType("GameBar"));
        Assert.Equal("demand", svc2.GetOriginalStartType("XboxNetApiSvc")); // removed
        Assert.Equal("demand", svc2.GetOriginalStartType("SomeManual"));
        Assert.Equal("auto", svc2.GetOriginalStartType("NewSvc"));
    }

    // ── ProcessControlResult ────────────────────────────────────────────

    [Fact]
    public void ProcessControlResult_SuccessHasCorrectState()
    {
        var result = ProcessControlResult.CreateSuccess("Done.");
        Assert.True(result.Success);
        Assert.Equal("Done.", result.Message);
    }

    [Fact]
    public void ProcessControlResult_FailureHasCorrectState()
    {
        var result = ProcessControlResult.CreateFailure("Service not found.");
        Assert.False(result.Success);
        Assert.Equal("Service not found.", result.Message);
    }

    [Fact]
    public void ProcessControlResult_EmptyMessageGetsDefault()
    {
        Assert.Equal("Operation succeeded.", ProcessControlResult.CreateSuccess("").Message);
        Assert.Equal("Operation failed.", ProcessControlResult.CreateFailure("").Message);
    }

    [Fact]
    public void ProcessControlResult_NullMessageGetsDefault()
    {
        Assert.Equal("Operation succeeded.", ProcessControlResult.CreateSuccess(null!).Message);
        Assert.Equal("Operation failed.", ProcessControlResult.CreateFailure(null!).Message);
    }
}
