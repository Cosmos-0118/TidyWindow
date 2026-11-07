using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// One number from a check: the middle result, how much the rounds moved (a measure of noise), and every round, so a later comparison can see whether
/// two checks overlap. <paramref name="Rounds"/> is missing in checks saved by older versions.
/// </summary>
public sealed record ProbeMetric(string Id, double Value, double Spread, IReadOnlyList<double>? Rounds = null);

/// <summary>
/// One check on this PC, with the settings that were in force and how it went, so two checks can be compared fairly.
/// <paramref name="Method"/> is the version of the tests (checks made by different versions are never compared; 0 means saved before versions existed),
/// <paramref name="Busy"/> is how much of the processor other programs used while it ran, and <paramref name="OnBattery"/> whether the PC ran on battery.
/// The last three are missing in checks saved by older versions.
/// </summary>
public sealed record ProbeRun(DateTimeOffset When, string Plan, string MemoryIntegrity, IReadOnlyList<ProbeMetric> Metrics, int Method = 0, double? Busy = null, bool? OnBattery = null);

/// <summary>What the Lab remembers: the power plan to go back to, and the newest checks.</summary>
public sealed class LabStore(string folder)
{
    private const int KeepRuns = 30;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string PowerPath => Path.Combine(folder, "power.json");
    private string RunsPath => Path.Combine(folder, "checks.json");

    public PowerNote? GetPower() { lock (gate) return Read<PowerNote>(PowerPath) is { PreviousId: not null, PreviousName: not null, SwitchedToId: not null, SwitchedToName: not null } note ? note : null; }
    public void SetPower(PowerNote note) { lock (gate) Write(PowerPath, note); }

    public void ClearPower()
    {
        lock (gate)
        {
            try { File.Delete(PowerPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* An old note is harmless; it is replaced on the next switch. */ }
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ProbeRun> Runs() { lock (gate) return Sound(Read<List<ProbeRun>>(RunsPath)).OrderByDescending(x => x.When).ToList(); }

    public void AddRun(ProbeRun run)
    {
        lock (gate)
        {
            var list = Sound(Read<List<ProbeRun>>(RunsPath));
            list.Add(run);
            Write(RunsPath, list.OrderByDescending(x => x.When).Take(KeepRuns).ToList());
        }
    }

    /// <summary>Only the checks that have every part. The file is the app's own, but an edited or half-written one must not take the page down.</summary>
    private static List<ProbeRun> Sound(List<ProbeRun>? runs) =>
        runs?.Where(x => x is { Plan: not null, MemoryIntegrity: not null, Metrics: not null } && x.Metrics.All(m => m is { Id: not null })).ToList() ?? [];

    public void ClearRuns()
    {
        lock (gate)
        {
            try { File.Delete(RunsPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static T? Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }

    private void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
