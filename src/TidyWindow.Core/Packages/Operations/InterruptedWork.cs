using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <param name="Kind">What was being done, in the form "installing".</param>
public sealed record InterruptedOperation(string Name, string Kind, string Source, DateTimeOffset Started);

/// <summary>
/// A note kept on disk for as long as an operation runs. If the app is closed, killed or crashes partway through one, the note is still there
/// at the next launch, which can then say that the work never finished instead of leaving a half-installed app to be found by accident.
/// </summary>
public sealed class InterruptedWork(string path)
{
    public void Begin(PackageOperation operation)
    {
        var source = operation.Fix is not null ? "Fixes" : ManagerDefinition.All.Single(x => x.Kind == operation.Manager).Name;
        var kind = operation.Kind switch { PackageOperationKind.Install => "installing", PackageOperationKind.Update => "updating", PackageOperationKind.Uninstall => "uninstalling", _ => "applying" };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new InterruptedOperation(operation.Name, kind, source, DateTimeOffset.Now)));
        }
        // A note that cannot be kept must never stop the work it describes.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The operation finished, one way or another, so there is nothing to report.</summary>
    public void End()
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>What the previous run was doing when it ended without finishing, if anything. Reading it clears it.</summary>
    public InterruptedOperation? Take()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var found = JsonSerializer.Deserialize<InterruptedOperation>(File.ReadAllText(path));
            File.Delete(path);
            return found is { Name.Length: > 0 } ? found : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            try { File.Delete(path); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }
}
