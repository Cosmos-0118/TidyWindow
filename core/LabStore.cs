using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>One number from a check, with how much it moved between the rounds (a measure of noise).</summary>
public sealed record ProbeMetric(string Id, double Value, double Spread);

/// <summary>One check on this PC, with the settings that were in force, so two checks can be compared fairly.</summary>
public sealed record ProbeRun(DateTimeOffset When, string Plan, string MemoryIntegrity, IReadOnlyList<ProbeMetric> Metrics);

/// <summary>What the Lab remembers: the power plan to go back to, and the newest checks.</summary>
public sealed class LabStore(string folder)
{
    private const int KeepRuns = 30;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string PowerPath => Path.Combine(folder, "power.json");
    private string RunsPath => Path.Combine(folder, "checks.json");

    public PowerNote? GetPower() { lock (gate) return Read<PowerNote>(PowerPath); }
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
    public IReadOnlyList<ProbeRun> Runs() { lock (gate) return (Read<List<ProbeRun>>(RunsPath) ?? []).OrderByDescending(x => x.When).ToList(); }

    public void AddRun(ProbeRun run)
    {
        lock (gate)
        {
            var list = Read<List<ProbeRun>>(RunsPath) ?? [];
            list.Add(run);
            Write(RunsPath, list.OrderByDescending(x => x.When).Take(KeepRuns).ToList());
        }
    }

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
