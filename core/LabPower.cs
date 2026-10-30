using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public sealed record PowerPlan(string Id, string Name, bool Active);

/// <summary>Runs powercfg with a fixed list of arguments and returns what it printed. Throws when powercfg fails.</summary>
public interface IPowerCfg
{
    string Run(IReadOnlyList<string> args);
}

/// <summary>
/// The only powercfg commands the Lab may run. Windows plans are chosen by id, never by a name typed by a person, and only a plan
/// that exists may be activated. There is no delete and no setting change: the Lab only switches between plans.
/// </summary>
public static partial class PowerGuard
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    /// <summary>The template Windows copies to make an Ultimate Performance plan. The copy gets a new id.</summary>
    public const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string CopyName = "Ultimate Performance (TidyWindow)";

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();

    public static bool IsId(string text) => Guid().IsMatch(text);

    public static bool Allows(IReadOnlyList<string> args) => args switch
    {
        ["/list"] or ["/getactivescheme"] => true,
        ["/setactive", var id] => IsId(id),
        ["/duplicatescheme", var template] => template.Equals(UltimateTemplate, StringComparison.OrdinalIgnoreCase) || template.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase),
        ["/changename", var id, var name] => IsId(id) && name == CopyName,
        _ => false
    };
}

/// <summary>The real thing. Runs powercfg.exe from the Windows folder, reading its output in the console code page.</summary>
[SupportedOSPlatform("windows")]
public sealed class PowerCfgProcess : IPowerCfg
{
    static PowerCfgProcess() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Run(IReadOnlyList<string> args)
    {
        if (!PowerGuard.Allows(args)) throw new InvalidOperationException("That power command is not allowed here.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("powercfg could not be started.");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(15_000)) { try { process.Kill(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { } throw new TimeoutException("powercfg did not answer."); }
            var text = output.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException((error.GetAwaiter().GetResult() + text).Trim() is { Length: > 0 } why ? why : $"powercfg failed with exit code {process.ExitCode}.");
            return text;
        }
        catch (Win32Exception ex) { throw new InvalidOperationException(ex.Message, ex); }
    }
}

public sealed record PowerNote(string PreviousId, string PreviousName, string SwitchedToId, string SwitchedToName, DateTimeOffset When);

/// <summary>What the power card shows: the active plan, the plan it would switch to, and whether to go back.</summary>
public sealed record PowerState(IReadOnlyList<PowerPlan> Plans, PowerPlan Active, PowerPlan? Fast, bool ActiveIsFast, PowerNote? Note);

/// <summary>
/// Switches between the normal power plan and a performance one, and back. The plan that was active is remembered on disk, so going back
/// works after the app was closed. No administrator rights are needed: Windows lets a normal user pick a plan that exists.
/// </summary>
public sealed partial class PowerMode(IPowerCfg powercfg, LabStore store)
{
    [GeneratedRegex(@"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+\((.*?)\)\s*(\*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Line();

    public static IReadOnlyList<PowerPlan> Parse(string output) =>
        output.Split('\n').Select(x => Line().Match(x)).Where(x => x.Success)
            .Select(x => new PowerPlan(x.Groups[1].Value.ToLowerInvariant(), x.Groups[2].Value.Trim(), x.Groups[3].Success)).ToList();

    /// <summary>A plan made for speed: an Ultimate Performance copy, or Windows' own High performance plan.</summary>
    public static bool IsFast(PowerPlan plan) =>
        plan.Id.Equals(PowerGuard.HighPerformance, StringComparison.OrdinalIgnoreCase) || plan.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase);

    public PowerState Read()
    {
        var plans = Parse(powercfg.Run(["/list"]));
        var active = Parse(powercfg.Run(["/getactivescheme"])).FirstOrDefault() ?? throw new InvalidOperationException("Windows did not say which power plan is active.");
        plans = plans.Select(x => x with { Active = x.Id == active.Id }).ToList();
        // Ultimate first, then High performance.
        var fast = plans.Where(IsFast).OrderByDescending(x => x.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        return new(plans, active with { Active = true }, fast, IsFast(active), store.GetPower());
    }

    public ActionResult Switch()
    {
        var state = Read();
        if (state.ActiveIsFast) return new(true, $"“{state.Active.Name}” is already active.");
        var target = state.Fast ?? Create();
        store.SetPower(new(state.Active.Id, state.Active.Name, target.Id, target.Name, DateTimeOffset.Now));
        try { Activate(target); }
        catch (InvalidOperationException) { store.ClearPower(); throw; }
        return new(true, $"Now using “{target.Name}”. “{state.Active.Name}” is remembered so you can go back.");
    }

    public ActionResult Restore()
    {
        var state = Read();
        var note = state.Note;
        var plan = note is null ? null : state.Plans.FirstOrDefault(x => x.Id == note.PreviousId);
        // If the plan that was there before is gone, the normal one is the safest place to go.
        plan ??= state.Plans.FirstOrDefault(x => x.Id.Equals(PowerGuard.Balanced, StringComparison.OrdinalIgnoreCase)) ?? state.Plans.FirstOrDefault(x => !IsFast(x));
        if (plan is null) return new(false, "There is no normal power plan to go back to.");
        if (state.Active.Id == plan.Id) { store.ClearPower(); return new(true, $"“{plan.Name}” is already active."); }
        Activate(plan);
        store.ClearPower();
        return new(true, $"Back to “{plan.Name}”.");
    }

    private PowerPlan Create()
    {
        var before = Parse(powercfg.Run(["/list"])).Select(x => x.Id).ToHashSet();
        string template = PowerGuard.UltimateTemplate;
        string output;
        // Some PCs do not offer Ultimate Performance; their High performance template still works.
        try { output = powercfg.Run(["/duplicatescheme", template]); }
        catch (InvalidOperationException) { template = PowerGuard.HighPerformance; output = powercfg.Run(["/duplicatescheme", template]); }
        var made = Parse(output).FirstOrDefault(x => !before.Contains(x.Id)) ?? throw new InvalidOperationException("Windows did not make the new power plan.");
        if (template == PowerGuard.UltimateTemplate)
        {
            powercfg.Run(["/changename", made.Id, PowerGuard.CopyName]);
            made = made with { Name = PowerGuard.CopyName };
        }
        return made;
    }

    private void Activate(PowerPlan plan)
    {
        powercfg.Run(["/setactive", plan.Id]);
        var now = Parse(powercfg.Run(["/getactivescheme"])).FirstOrDefault();
        if (now is null || now.Id != plan.Id) throw new InvalidOperationException("Windows did not switch the power plan.");
    }
}

public sealed record ActionResult(bool Ok, string Message);

/// <summary>Whether this PC runs on a battery, so a plan that never saves power can be flagged for laptops.</summary>
public static class PowerSource
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Status { public byte AcLine, BatteryFlag, BatteryPercent, SystemFlag; public int LifeTime, FullLifeTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out Status status);

    /// <summary>Battery flag 128 means there is none; 255 means Windows does not know, which is treated as none.</summary>
    public static bool HasBattery() => OperatingSystem.IsWindows() && GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);
}
