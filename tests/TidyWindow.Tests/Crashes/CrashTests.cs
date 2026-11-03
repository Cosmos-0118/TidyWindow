using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

internal static class CrashFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static long record;

    public static RawEvent Raw(string log, string provider, int id, DateTimeOffset time, params (string Name, string Value)[] data) =>
        new(log, ++record, id, provider, time, data.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)).ToList());

    // Field layouts are the ones Windows 11 writes.
    public static RawEvent Crash(string app, DateTimeOffset time, string module = "KERNELBASE.dll", string code = "c0000005", string version = "1.0.0.0", string report = "", string path = "") =>
        Raw("Application", "Application Error", 1000, time,
            ("AppName", app), ("AppVersion", version), ("AppTimeStamp", "6ada0000"), ("ModuleName", module), ("ModuleVersion", "10.0.26100.9818"), ("ModuleTimeStamp", "09f91ee4"),
            ("ExceptionCode", code), ("FaultingOffset", "00000000000c569a"), ("ProcessId", "0x7340"), ("ProcessCreationTime", "0x1dd6b4af7f3ec8b"),
            ("AppPath", path.Length > 0 ? path : $@"C:\Apps\{app}"), ("ModulePath", $@"C:\WINDOWS\System32\{module}"), ("IntegratorReportId", report), ("PackageFullName", ""), ("PackageRelativeAppId", ""));

    public static RawEvent Hang(string app, DateTimeOffset time) =>
        Raw("Application", "Application Hang", 1002, time,
            ("AppName", app), ("AppVersion", "21.0.9.0"), ("ProcessId", "0x6be0"), ("StartTime", "0x1dd6373fd84d153"), ("TerminationTime", "37"),
            ("ExeFileName", $@"C:\Apps\{app}"), ("ReportId", "1d517473-56f2-4050-b65c-d8e61da8a34d"), ("PackageFullName", ""), ("PackageRelativeAppId", ""), ("HangType", "Unknown"));

    public static RawEvent DotNet(string app, DateTimeOffset time) =>
        Raw("Application", ".NET Runtime", 1026, time, ("", $"Application: {app}\nCoreCLR Version: 10.0.1426.52326\n.NET Version: 10.0.14\nDescription: The process was terminated due to an unhandled exception.\nException Info: System.InvalidOperationException: no vault; cannot continue\n   at Roundtrip.Run() in C:\\x\\Roundtrip.cs:line 107\n"));

    public static RawEvent Report(DateTimeOffset time, string report, string name = "APPCRASH") =>
        Raw("Application", "Windows Error Reporting", 1001, time,
            ("Bucket", "1"), ("BucketType", "5"), ("EventName", name), ("Response", "Not available"), ("CabId", "0"), ("P1", "Smoke.exe"),
            ("StorePath", $@"\\?\C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_Smoke.exe_{report}"), ("ReportId", report));

    public static CrashEvent Parsed(RawEvent raw) => CrashParser.Parse(raw) ?? throw new InvalidOperationException("not understood");
}

public class CrashParserTests
{
    [Fact]
    public void An_application_error_reads_every_field()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Crash("Smoke.exe", CrashFixtures.Now, report: "4f4e336f"));
        Assert.Equal(CrashKind.AppCrash, e.Kind);
        Assert.Equal("Smoke.exe", e.App);
        Assert.Equal("Smoke", e.AppName);
        Assert.Equal("KERNELBASE.dll", e.Module);
        Assert.Equal("0xC0000005", e.Code);
        Assert.Equal("Access violation", e.CodeName);
        Assert.Equal("29504", e.ProcessId);
        Assert.Equal("4f4e336f", e.ReportId);
        Assert.Equal("smoke.exe", e.AppKey);
        Assert.Equal("Smoke crashed in KERNELBASE.dll", e.Headline);
        Assert.NotEmpty(e.Explanation);
    }

    [Fact]
    public void A_dotnet_exception_code_is_named()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Crash("Smoke.exe", CrashFixtures.Now, code: "e0434352"));
        Assert.Equal("0xE0434352", e.Code);
        Assert.Equal(".NET exception", e.CodeName);
    }

    [Fact]
    public void A_hang_reads_its_report_and_does_not_invent_a_hang_type()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Hang("javaw.exe", CrashFixtures.Now));
        Assert.Equal(CrashKind.AppHang, e.Kind);
        Assert.Equal(@"C:\Apps\javaw.exe", e.AppPath);
        Assert.Equal("1d517473-56f2-4050-b65c-d8e61da8a34d", e.ReportId);
        Assert.Equal("", e.Detail);
        Assert.Equal("javaw stopped responding", e.Headline);
    }

    [Fact]
    public void The_dotnet_runtime_record_keeps_the_exception_and_its_stack()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.DotNet("Smoke.exe", CrashFixtures.Now));
        Assert.Equal("Smoke.exe", e.App);
        Assert.Equal("System.InvalidOperationException: no vault; cannot continue", e.Managed);
        Assert.Contains("Roundtrip.Run()", e.Detail);
        Assert.DoesNotContain("CoreCLR Version", e.Detail);
    }

    [Fact]
    public void Other_software_that_reuses_an_event_number_is_ignored()
    {
        // Real events on this PC: a vendor service logs its routine status as 1000, 1001 and 1002.
        Assert.Null(CrashParser.Parse(CrashFixtures.Raw("Application", "CodexSandboxService.OpenAI.Codex", 1000, CrashFixtures.Now)));
        Assert.Null(CrashParser.Parse(CrashFixtures.Raw("Application", "CodexSandboxService.OpenAI.Codex", 1002, CrashFixtures.Now)));
    }

    [Fact]
    public void A_report_for_a_crash_keeps_only_its_folder_without_the_path_prefix()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Report(CrashFixtures.Now, "abc"));
        Assert.Equal(CrashKind.Report, e.Kind);
        Assert.Equal("abc", e.ReportId);
        Assert.StartsWith(@"C:\ProgramData", e.ReportFolder);
    }

    [Fact]
    public void A_kernel_event_report_is_a_problem_of_its_own()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Raw("Application", "Windows Error Reporting", 1001, CrashFixtures.Now, ("EventName", "LiveKernelEvent"), ("P1", "141"), ("ReportId", "r1")));
        Assert.Equal(CrashKind.DriverReset, e.Kind);
        Assert.Equal("0x141", e.Code);
        Assert.Contains("GPU", e.Headline);
    }

    [Fact]
    public void A_kernel_event_nobody_can_explain_is_not_shown()
    {
        Assert.Null(CrashParser.Parse(CrashFixtures.Raw("Application", "Windows Error Reporting", 1001, CrashFixtures.Now, ("EventName", "LiveKernelEvent"), ("P1", "1b8"), ("ReportId", "r1"))));
    }

    [Fact]
    public void Kernel_power_without_a_stop_code_is_an_unexpected_shutdown_and_notes_the_power_button()
    {
        var raw = CrashFixtures.Raw("System", "Microsoft-Windows-Kernel-Power", 41, CrashFixtures.Now, ("BugcheckCode", "0"), ("PowerButtonTimestamp", "134000"), ("SleepInProgress", "0"));
        var e = CrashFixtures.Parsed(raw);
        Assert.Equal(CrashKind.UnexpectedShutdown, e.Kind);
        Assert.Contains("power button", e.Detail);
        Assert.DoesNotContain("sleep", e.Detail);
    }

    [Fact]
    public void Kernel_power_with_a_stop_code_is_a_blue_screen_and_the_decimal_code_becomes_hex()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Raw("System", "Microsoft-Windows-Kernel-Power", 41, CrashFixtures.Now, ("BugcheckCode", "278"), ("BugcheckParameter1", "0x1")));
        Assert.Equal(CrashKind.BlueScreen, e.Kind);
        Assert.Equal("0x00000116", e.Code);
        Assert.Equal("VIDEO_TDR_FAILURE", e.CodeName);
    }

    [Fact]
    public void A_blue_screen_report_reads_the_code_dump_and_report_id()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Raw("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, CrashFixtures.Now,
            ("param1", "0x0000009f (0x3, 0xffff, 0x0, 0x1)"), ("param2", @"C:\WINDOWS\MEMORY.DMP"), ("param3", "rid")));
        Assert.Equal("0x0000009F", e.Code);
        Assert.Equal("DRIVER_POWER_STATE_FAILURE", e.CodeName);
        Assert.Equal(@"C:\WINDOWS\MEMORY.DMP", e.DumpFile);
        Assert.Contains("0x3, 0xffff", e.Detail);
    }

    [Fact]
    public void Service_events_name_the_service()
    {
        var crash = CrashFixtures.Parsed(CrashFixtures.Raw("System", "Service Control Manager", 7034, CrashFixtures.Now, ("param1", "MSI_Case_Service"), ("param2", "1")));
        Assert.Equal(CrashKind.ServiceFailure, crash.Kind);
        Assert.Equal("MSI_Case_Service stopped unexpectedly", crash.Headline);
        var failed = CrashFixtures.Parsed(CrashFixtures.Raw("System", "Service Control Manager", 7000, CrashFixtures.Now, ("param1", "Steam Client Service"), ("param2", "%%1053")));
        Assert.Equal("Steam Client Service failed to start: it did not respond in time", failed.Headline);
        var unknown = CrashFixtures.Parsed(CrashFixtures.Raw("System", "Service Control Manager", 7000, CrashFixtures.Now, ("param1", "Odd"), ("param2", "%%99999")));
        Assert.Equal("Odd failed to start", unknown.Headline);
        var start = CrashFixtures.Parsed(CrashFixtures.Raw("System", "Service Control Manager", 7009, CrashFixtures.Now, ("param1", "30000"), ("param2", "Spooler")));
        Assert.Equal("Spooler timed out while starting (waited 30 s)", start.Headline);
    }

    [Fact]
    public void An_event_that_throws_while_reading_is_skipped_not_fatal()
    {
        // Too few fields everywhere: every accessor must cope.
        Assert.NotNull(CrashParser.Parse(CrashFixtures.Raw("Application", "Application Error", 1000, CrashFixtures.Now)));
        Assert.Null(CrashParser.Parse(CrashFixtures.Raw("Application", ".NET Runtime", 1026, CrashFixtures.Now, ("", "not the expected text"))));
    }

    [Theory]
    [InlineData("c0000005", "0xC0000005")]
    [InlineData("0xc0000005", "0xC0000005")]
    [InlineData("0x0000009f", "0x0000009F")]
    [InlineData("9f", "0x9F")]
    [InlineData("hello", "hello")]
    public void Codes_have_one_spelling(string input, string expected) => Assert.Equal(expected, CrashKnowledge.NormalizeCode(input));

    [Fact]
    public void Event_xml_is_read_in_one_pass()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="Application Error" /><EventID Qualifiers="0">1000</EventID><TimeCreated SystemTime="2026-11-03T13:56:39.1234567Z" /><EventRecordID>77</EventRecordID></System>
              <EventData><Data Name="AppName">a.exe</Data><Data Name="Empty" /><Data>loose &amp; text</Data></EventData>
            </Event>
            """;
        var e = EventXml.Parse(xml, "Application")!;
        Assert.Equal("Application Error", e.Provider);
        Assert.Equal(1000, e.EventId);
        Assert.Equal(77, e.RecordId);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 13, 56, 39, TimeSpan.Zero).AddTicks(1234567), e.Time);
        Assert.Equal("a.exe", e.Get("AppName"));
        Assert.Equal("", e.Get("Empty"));
        Assert.Equal("loose & text", e.At(2));
        Assert.Null(EventXml.Parse("<not-xml", "Application"));
        Assert.Null(EventXml.Parse("", "Application"));
    }

    [Fact]
    public void Event_xml_refuses_a_dtd()
    {
        Assert.Null(EventXml.Parse("<!DOCTYPE e [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><Event><System><Provider Name=\"p\"/></System></Event>", "Application"));
    }
}

public class CrashDigestTests
{
    private static readonly DateTimeOffset T = CrashFixtures.Now;
    private static CrashEvent P(RawEvent raw) => CrashFixtures.Parsed(raw);

    [Fact]
    public void A_dotnet_crash_recorded_twice_is_one_crash_with_its_exception()
    {
        var events = new[] { P(CrashFixtures.Crash("Smoke.exe", T, code: "e0434352")), P(CrashFixtures.DotNet("Smoke.exe", T.AddSeconds(-1))) };
        var merged = CrashDigest.Merge(events);
        var only = Assert.Single(merged);
        Assert.Equal("System.InvalidOperationException: no vault; cannot continue", only.Managed);
        Assert.Contains("Roundtrip.Run()", only.Detail);
        Assert.Equal("Smoke crashed: System.InvalidOperationException: no vault; cannot continue", only.Headline);
    }

    [Fact]
    public void A_dotnet_record_far_from_any_windows_record_stays_its_own_crash()
    {
        var merged = CrashDigest.Merge([P(CrashFixtures.Crash("Smoke.exe", T)), P(CrashFixtures.DotNet("Smoke.exe", T.AddMinutes(-5)))]);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Two_crashes_of_one_app_do_not_borrow_one_dotnet_record()
    {
        var merged = CrashDigest.Merge([P(CrashFixtures.Crash("a.exe", T)), P(CrashFixtures.Crash("a.exe", T.AddSeconds(5))), P(CrashFixtures.DotNet("a.exe", T))]);
        Assert.Equal(2, merged.Count);
        Assert.Single(merged, x => x.Managed.Length > 0);
    }

    [Fact]
    public void A_crash_gets_its_report_folder_and_the_report_record_is_hidden()
    {
        var merged = CrashDigest.Merge([P(CrashFixtures.Crash("a.exe", T, report: "rid-1")), P(CrashFixtures.Report(T.AddSeconds(2), "rid-1"))]);
        var only = Assert.Single(merged);
        Assert.EndsWith("rid-1", only.ReportFolder);
    }

    [Fact]
    public void One_blue_screen_written_by_three_components_is_listed_once_with_the_dump()
    {
        var events = new[]
        {
            P(CrashFixtures.Raw("System", "Microsoft-Windows-Kernel-Power", 41, T, ("BugcheckCode", "159"))),
            P(CrashFixtures.Raw("System", "EventLog", 6008, T.AddSeconds(-30))),
            P(CrashFixtures.Raw("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, T.AddMinutes(1), ("param1", "0x0000009f (0x1)"), ("param2", @"C:\WINDOWS\MEMORY.DMP"), ("param3", "r")))
        };
        var only = Assert.Single(CrashDigest.Merge(events));
        Assert.Equal(CrashKind.BlueScreen, only.Kind);
        Assert.Equal(@"C:\WINDOWS\MEMORY.DMP", only.DumpFile);
    }

    [Fact]
    public void A_device_logging_the_same_problem_in_a_burst_is_one_entry_with_a_count()
    {
        RawEvent Reset(DateTimeOffset time) => CrashFixtures.Raw("System", "Display", 4101, time, ("", "nvlddmkm"), ("", @"\Device\Video3"));
        var burst = Enumerable.Range(0, 40).Select(i => P(Reset(T.AddSeconds(-i)))).Concat([P(Reset(T.AddHours(-3)))]);
        var merged = CrashDigest.Merge(burst);
        Assert.Equal(2, merged.Count);
        Assert.Equal(40, merged[0].Repeats);
        Assert.Equal(T, merged[0].Time);
        Assert.Equal(1, merged[1].Repeats);
    }

    [Fact]
    public void App_crashes_in_a_row_are_never_folded_together()
    {
        var merged = CrashDigest.Merge(Enumerable.Range(0, 5).Select(i => P(CrashFixtures.Crash("a.exe", T.AddSeconds(-i * 60)))));
        Assert.Equal(5, merged.Count);
        Assert.All(merged, x => Assert.Equal(1, x.Repeats));
    }

    [Fact]
    public void Shutdowns_hours_apart_are_separate()
    {
        var events = new[] { P(CrashFixtures.Raw("System", "EventLog", 6008, T)), P(CrashFixtures.Raw("System", "EventLog", 6008, T.AddHours(-3))) };
        Assert.Equal(2, CrashDigest.Merge(events).Count);
    }

    [Fact]
    public void Apps_are_ranked_by_trouble_and_count_crashes_hangs_signatures_and_days()
    {
        var events = CrashDigest.Merge(new[]
        {
            P(CrashFixtures.Crash("Chrome.exe", T.AddHours(-1), "chrome.dll", "c0000005", "130.0")),
            P(CrashFixtures.Crash("chrome.exe", T.AddHours(-2), "chrome.dll", "c0000005", "129.0")),
            P(CrashFixtures.Crash("chrome.exe", T.AddDays(-2), "nvwgf2umx.dll", "c0000409", "129.0")),
            P(CrashFixtures.Hang("chrome.exe", T.AddDays(-1))),
            P(CrashFixtures.Crash("notepad.exe", T.AddDays(-40)))
        });
        var apps = CrashDigest.Apps(events, T, 7);
        var chrome = apps[0];
        Assert.Equal("chrome.exe", chrome.Key);
        Assert.Equal((3, 1), (chrome.Crashes, chrome.Hangs));
        Assert.Equal(4, chrome.Total);
        Assert.Equal("chrome.dll · Access violation", chrome.Signatures[0].Label);
        Assert.Equal(2, chrome.Signatures[0].Count);
        Assert.Equal(["130.0", "129.0", "21.0.9.0"], chrome.Versions);
        Assert.Equal(7, chrome.Daily.Count);
        Assert.Equal(2, chrome.Daily[6]);
        Assert.Equal(1, chrome.Daily[5]);
        Assert.Equal(1, chrome.Daily[4]);
        Assert.Equal(1, apps[1].Total);
    }

    [Fact]
    public void Windows_problems_are_not_attributed_to_an_app()
    {
        var events = CrashDigest.Merge([P(CrashFixtures.Raw("System", "Service Control Manager", 7034, T, ("param1", "Spooler")))]);
        Assert.Empty(CrashDigest.Apps(events, T, 7));
    }

    [Fact]
    public void Several_apps_failing_in_one_file_is_called_out_but_a_generic_file_is_not()
    {
        var shared = new[] { "a.exe", "b.exe", "c.exe" }.Select((x, i) => P(CrashFixtures.Crash(x, T.AddHours(-i), "nvwgf2umx.dll"))).ToList();
        Assert.Contains(CrashDigest.Insights(shared), x => x.Title.Contains("3 different apps crashed in nvwgf2umx.dll"));
        var generic = new[] { "a.exe", "b.exe", "c.exe" }.Select((x, i) => P(CrashFixtures.Crash(x, T.AddHours(-i), "ntdll.dll"))).ToList();
        Assert.Empty(CrashDigest.Insights(generic));
    }

    [Fact]
    public void A_repeating_crash_and_a_crash_loop_are_noticed()
    {
        var events = Enumerable.Range(0, 3).Select(i => P(CrashFixtures.Crash("app.exe", T.AddMinutes(-i), "app.dll"))).ToList();
        var insights = CrashDigest.Insights(events);
        Assert.Contains(insights, x => x.Title == "app keeps crashing the same way");
        Assert.Contains(insights, x => x.Title == "app crashed 3 times within minutes");
    }

    [Fact]
    public void Insights_are_capped_and_empty_for_a_quiet_pc()
    {
        Assert.Empty(CrashDigest.Insights([]));
        Assert.Empty(CrashDigest.Insights([P(CrashFixtures.Crash("a.exe", T))]));
    }
}

public class CrashHistoryTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "tidy-crash-" + Guid.NewGuid().ToString("N"));
    private static CrashEvent Event(string id, DateTimeOffset time) => new() { Id = id, Time = time, Kind = CrashKind.AppCrash, App = "a.exe", Detail = "line one\nline \"two\"" };

    [Fact]
    public void Saved_records_and_the_scan_mark_come_back_after_a_restart()
    {
        var folder = Folder();
        try
        {
            var first = new CrashHistory(folder, () => CrashFixtures.Now);
            Assert.Equal(2, first.Add([Event("a", CrashFixtures.Now.AddDays(-1)), Event("b", CrashFixtures.Now.AddDays(-2))]));
            first.MarkScanned(CrashFixtures.Now);
            first.MarkSeen(CrashFixtures.Now.AddHours(-1));
            var second = new CrashHistory(folder, () => CrashFixtures.Now);
            Assert.Equal(["a", "b"], second.All().Select(x => x.Id));
            Assert.Equal("line one\nline \"two\"", second.All()[0].Detail);
            Assert.Equal(CrashFixtures.Now, second.ScannedUpTo);
            Assert.Equal(CrashFixtures.Now.AddHours(-1), second.SeenUpTo);
            Assert.Null(second.StorageError);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_closed_observation_stays_closed_after_a_restart_until_history_is_cleared()
    {
        var folder = Folder();
        try
        {
            var first = new CrashHistory(folder, () => CrashFixtures.Now);
            first.Dismiss("3 apps crashed in nvwgf2umx.dll");
            first.Dismiss("3 apps crashed in nvwgf2umx.dll");
            var second = new CrashHistory(folder, () => CrashFixtures.Now);
            Assert.True(second.IsDismissed("3 apps crashed in nvwgf2umx.dll"));
            Assert.False(second.IsDismissed("4 apps crashed in nvwgf2umx.dll"));
            second.Clear();
            Assert.False(new CrashHistory(folder, () => CrashFixtures.Now).IsDismissed("3 apps crashed in nvwgf2umx.dll"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void Only_the_most_recent_closed_observations_are_kept()
    {
        var history = new CrashHistory(null, () => CrashFixtures.Now);
        for (var i = 0; i <= CrashHistory.MaxDismissed; i++) history.Dismiss("n" + i);
        Assert.False(history.IsDismissed("n0"));
        Assert.True(history.IsDismissed("n" + CrashHistory.MaxDismissed));
    }

    [Fact]
    public void The_same_record_is_not_added_twice()
    {
        var history = new CrashHistory(null, () => CrashFixtures.Now);
        Assert.Equal(1, history.Add([Event("a", CrashFixtures.Now)]));
        Assert.Equal(0, history.Add([Event("a", CrashFixtures.Now)]));
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Old_records_are_forgotten()
    {
        var history = new CrashHistory(null, () => CrashFixtures.Now);
        history.Add([Event("old", CrashFixtures.Now - CrashHistory.Retention - TimeSpan.FromDays(1)), Event("new", CrashFixtures.Now)]);
        Assert.Equal(["new"], history.All().Select(x => x.Id));
    }

    [Fact]
    public void The_history_is_bounded()
    {
        var history = new CrashHistory(null, () => CrashFixtures.Now);
        history.Add(Enumerable.Range(0, CrashHistory.Capacity + 50).Select(i => Event("e" + i, CrashFixtures.Now.AddSeconds(-i))));
        Assert.Equal(CrashHistory.Capacity, history.Count);
        Assert.DoesNotContain(history.All(), x => x.Id == "e" + (CrashHistory.Capacity + 49));
    }

    [Fact]
    public void A_damaged_line_is_skipped_and_clearing_forgets_everything()
    {
        var folder = Folder();
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "crashes.jsonl"), "{ not json\n" + "{\"Id\":\"ok\",\"Time\":\"2026-11-02T00:00:00+00:00\",\"Kind\":\"AppHang\"}\n");
            var history = new CrashHistory(folder, () => CrashFixtures.Now);
            Assert.Equal(["ok"], history.All().Select(x => x.Id));
            history.Clear();
            Assert.Equal(0, history.Count);
            Assert.Null(new CrashHistory(folder, () => CrashFixtures.Now).ScannedUpTo);
            Assert.Empty(new CrashHistory(folder, () => CrashFixtures.Now).All());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_reported_not_thrown()
    {
        var file = Path.GetTempFileName();
        try
        {
            // A file where the folder should be.
            var history = new CrashHistory(Path.Combine(file, "inside"), () => CrashFixtures.Now);
            history.Add([Event("a", CrashFixtures.Now)]);
            Assert.NotNull(history.StorageError);
            Assert.Equal(1, history.Count);
        }
        finally { File.Delete(file); }
    }
}

internal sealed class FakeEventSource : ICrashEventSource
{
    public Dictionary<string, List<RawEvent>> Logs = new() { ["Application"] = [], ["System"] = [] };
    public List<(string Log, string Query, int Limit)> Calls = [];
    public Dictionary<string, EventSourceException> Failures = [];

    public IReadOnlyList<RawEvent> Read(string log, string query, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls.Add((log, query, limit));
        if (Failures.TryGetValue(log, out var failure)) throw failure;
        return Logs.GetValueOrDefault(log) ?? [];
    }
}

public class CrashScannerTests
{
    private static readonly DateTimeOffset T = CrashFixtures.Now;

    [Fact]
    public void The_first_scan_looks_back_ninety_days_and_later_ones_only_since_the_last()
    {
        var source = new FakeEventSource();
        var history = new CrashHistory(null, () => T);
        var scanner = new CrashScanner(source, history, () => T);
        scanner.Scan(CancellationToken.None);
        Assert.All(source.Calls, c => Assert.Contains(T.AddDays(-90).UtcDateTime.ToString("yyyy-MM-dd"), c.Query));
        Assert.Equal(T, history.ScannedUpTo);

        source.Calls.Clear();
        new CrashScanner(source, history, () => T.AddHours(2)).Scan(CancellationToken.None);
        Assert.All(source.Calls, c => Assert.Contains("2026-11-03T11:55:00.000Z", c.Query));
    }

    [Fact]
    public void A_full_scan_ignores_the_mark()
    {
        var source = new FakeEventSource();
        var history = new CrashHistory(null, () => T);
        history.MarkScanned(T);
        new CrashScanner(source, history, () => T).Scan(CancellationToken.None, full: true);
        Assert.All(source.Calls, c => Assert.Contains("2026-08-05", c.Query));
    }

    [Fact]
    public void Events_are_read_stored_and_counted_once_even_when_scans_overlap()
    {
        var source = new FakeEventSource();
        source.Logs["Application"].Add(CrashFixtures.Crash("a.exe", T.AddMinutes(-1)));
        source.Logs["System"].Add(CrashFixtures.Raw("System", "Service Control Manager", 7034, T.AddMinutes(-2), ("param1", "Spooler")));
        var history = new CrashHistory(null, () => T);
        var scanner = new CrashScanner(source, history, () => T);
        Assert.Equal(2, scanner.Scan(CancellationToken.None).Added);
        Assert.Equal(0, scanner.Scan(CancellationToken.None).Added);
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void A_log_that_fails_is_reported_and_retried_but_the_others_still_count()
    {
        var source = new FakeEventSource();
        source.Logs["Application"].Add(CrashFixtures.Crash("a.exe", T.AddMinutes(-1)));
        source.Failures["System"] = new("denied", 5);
        var history = new CrashHistory(null, () => T);
        var result = new CrashScanner(source, history, () => T).Scan(CancellationToken.None);
        Assert.Equal(1, result.Added);
        Assert.Equal("Windows would not let TidyWindow read the System log.", Assert.Single(result.Problems));
        Assert.Null(history.ScannedUpTo);
    }

    [Fact]
    public void A_log_this_pc_does_not_have_is_not_a_problem()
    {
        var source = new FakeEventSource();
        source.Failures["System"] = new("no channel", 15007);
        var history = new CrashHistory(null, () => T);
        var result = new CrashScanner(source, history, () => T).Scan(CancellationToken.None);
        Assert.Empty(result.Problems);
        Assert.Equal(T, history.ScannedUpTo);
    }

    [Fact]
    public void A_cancelled_scan_stops_and_keeps_the_mark()
    {
        var source = new FakeEventSource();
        var history = new CrashHistory(null, () => T);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new CrashScanner(source, history, () => T).Scan(cancel.Token));
        Assert.Null(history.ScannedUpTo);
    }

    [Fact]
    public void The_queries_name_the_component_as_well_as_the_number()
    {
        var source = new FakeEventSource();
        new CrashScanner(source, new CrashHistory(null, () => T), () => T).Scan(CancellationToken.None);
        // One query for the plain Application sources, one for the narrowed report records, one for System.
        Assert.Equal(3, source.Calls.Count);
        var application = source.Calls.First(c => c.Log == "Application").Query;
        Assert.Contains("Provider[@Name='Application Error'] and (EventID=1000)", application);
        Assert.Contains("Provider[@Name='.NET Runtime'] and (EventID=1026)", application);
        var reports = source.Calls.Single(c => c.Query.Contains("Windows Error Reporting")).Query;
        Assert.Contains("EventData[Data[@Name='EventName']='APPCRASH' or", reports);
        Assert.Contains("'LiveKernelEvent'", reports);
        var system = source.Calls.First(c => c.Log == "System").Query;
        Assert.Contains("Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)", system);
        Assert.Contains("Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153)", system);
        Assert.All(source.Calls, c => Assert.Equal(4000, c.Limit));
    }
}

public class CrashReportTests
{
    [Fact]
    public void A_report_names_the_facts_and_hides_the_account_name()
    {
        var e = CrashFixtures.Parsed(CrashFixtures.Crash("a.exe", CrashFixtures.Now, "x.dll", "c0000005", "2.0", path: @"C:\Users\someone\AppData\a.exe"));
        var text = CrashReport.Format([e], @"C:\Users\someone");
        Assert.Contains("CRASH  a crashed in x.dll", text);
        Assert.Contains("Code: 0xC0000005 (Access violation)", text);
        Assert.Contains(@"Path: %USERPROFILE%\AppData\a.exe", text);
        Assert.DoesNotContain("someone", text);
    }

    [Fact]
    public void An_app_summary_lists_its_repeating_failures()
    {
        var events = Enumerable.Range(0, 3).Select(i => CrashFixtures.Parsed(CrashFixtures.Crash("a.exe", CrashFixtures.Now.AddHours(-i), "x.dll"))).ToList();
        var app = CrashDigest.Apps(events, CrashFixtures.Now, 7)[0];
        var text = CrashReport.Format(app, @"C:\Users\someone");
        Assert.StartsWith("a: 3 crashes, 0 not responding", text);
        Assert.Contains("3x x.dll · Access violation", text);
    }

    [Fact]
    public void The_web_search_uses_the_words_other_people_wrote()
    {
        Assert.Equal("a.exe x.dll Access violation crash", CrashReport.SearchText(CrashFixtures.Parsed(CrashFixtures.Crash("a.exe", CrashFixtures.Now, "x.dll"))));
        Assert.Equal("a.exe not responding", CrashReport.SearchText(CrashFixtures.Parsed(CrashFixtures.Hang("a.exe", CrashFixtures.Now))));
    }
}

public class CrashSearchTests
{
    private static readonly DateTimeOffset T = CrashFixtures.Now;
    private static CrashEvent P(RawEvent raw) => CrashFixtures.Parsed(raw);

    private static readonly CrashSearchIndex Index = new(CrashDigest.Merge(new[]
    {
        P(CrashFixtures.Crash("chrome.exe", T.AddHours(-1), "chrome.dll", "c0000005", "130.0.1")),
        P(CrashFixtures.Crash("chrome.exe", T.AddDays(-3), "nvwgf2umx.dll", "c0000409", "129.0.1")),
        P(CrashFixtures.Crash("Code.exe", T.AddHours(-5), "ntdll.dll", "c0000374", "1.95")),
        P(CrashFixtures.Hang("javaw.exe", T.AddDays(-1))),
        P(CrashFixtures.Raw("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, T.AddDays(-2), ("param1", "0x00000124 (0x0)"), ("param2", @"C:\WINDOWS\MEMORY.DMP"), ("param3", "r"))),
        P(CrashFixtures.Raw("System", "Service Control Manager", 7034, T.AddHours(-30), ("param1", "Spooler")))
    }));

    private static string[] Find(string text, DateTimeOffset? from = null) => Index.Search(CrashQuery.Parse(text, T), from).Select(x => x.AppName.Length > 0 ? x.AppName : x.Kind.ToString()).ToArray();

    [Fact]
    public void An_empty_search_returns_everything_newest_first()
    {
        var all = Index.Search(CrashQuery.Parse("", T));
        Assert.Equal(6, all.Count);
        Assert.Equal(all.OrderByDescending(x => x.Time), all);
    }

    [Fact]
    public void Words_all_have_to_match_and_the_app_name_ranks_first()
    {
        var found = Index.Search(CrashQuery.Parse("chrome access", T));
        Assert.Single(found);
        Assert.Equal("chrome.dll", found[0].Module);
        Assert.Equal(["chrome", "chrome"], Find("chrome"));
    }

    [Fact]
    public void A_typo_is_forgiven_but_a_short_word_is_not_guessed_at()
    {
        Assert.Equal(["chrome", "chrome"], Find("chorme"));
        Assert.Equal(["chrome", "chrome"], Find("crhome"));
        Assert.Empty(Find("xyzzy"));
        Assert.Empty(Find("chx"));
        Assert.Equal(["chrome", "chrome"], Find("chr"));
    }

    [Fact]
    public void Plain_words_find_things_by_what_they_mean()
    {
        Assert.Equal(["javaw"], Find("freeze"));
        Assert.Equal(["BlueScreen"], Find("bsod"));
        Assert.Equal(["BlueScreen"], Find("blue screen"));
        Assert.Equal(["Code"], Find("heap corruption"));
    }

    [Fact]
    public void Codes_match_with_or_without_the_prefix_and_by_name()
    {
        Assert.Equal(["chrome"], Find("code:c0000005"));
        Assert.Equal(["chrome"], Find("code:0xC0000005"));
    }

    [Fact]
    public void Exclusions_and_phrases_work()
    {
        Assert.Equal(["Code", "javaw"], Find("-chrome kind:app"));
        Assert.Equal(["chrome"], Find("\"access violation\""));
        Assert.Empty(Find("\"violation access\""));
    }

    [Fact]
    public void Fields_narrow_and_alternatives_combine()
    {
        Assert.Equal(["chrome"], Find("app:chrome module:nvwgf2umx"));
        Assert.Equal(["chrome", "Code", "chrome"], Find("module:chrome module:ntdll module:nvwgf2umx"));
        Assert.Equal(["chrome"], Find("version:130"));
        Assert.Equal(["javaw"], Find("kind:hang"));
        Assert.Equal(["Spooler", "BlueScreen"], Find("kind:windows"));
        Assert.Equal(["chrome"], Find("kind:crash path:apps version:129"));
    }

    [Fact]
    public void Time_filters_read_relative_and_absolute_dates()
    {
        Assert.Equal(["chrome", "Code"], Find("since:6h"));
        Assert.Equal(["chrome", "Code", "javaw", "Spooler"], Find("last:40h"));
        Assert.Equal(["chrome"], Find("before:2d app:chrome"));
        Assert.Equal(["chrome", "Code"], Find("since:today"));
        Assert.Equal(6, Find("since:2026-10-01").Length);
        Assert.Empty(Find("since:2026-11-04"));
        Assert.Equal(6, Find("before:2026-11-04").Length);
    }

    [Fact]
    public void The_pages_own_time_range_applies_too()
    {
        Assert.Equal(["chrome", "Code"], Find("", T.AddHours(-6)));
    }

    [Fact]
    public void A_colon_that_is_not_a_field_stays_text_and_the_search_says_how_it_was_read()
    {
        Assert.Empty(Find("nosuchfield:thing"));
        Assert.Equal(["chrome", "chrome", "Code", "javaw"], Find(@"c:\apps").OrderBy(x => x).ToArray());
        var query = CrashQuery.Parse("app:chrome -edge since:7d", T);
        Assert.Equal(["app: chrome", "not “edge”", "since 7 days ago"], query.Chips);
        Assert.False(query.IsEmpty);
        Assert.True(CrashQuery.Parse("   ", T).IsEmpty);
    }

    [Fact]
    public void An_unreadable_time_is_searched_as_text()
    {
        Assert.Empty(Find("since:soonish"));
    }
}
