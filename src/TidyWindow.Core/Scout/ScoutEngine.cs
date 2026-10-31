using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace TidyWindow.Next.Core;

public sealed record ScoutEngineInstallation(string? ExecutablePath, bool UserProcessRunning, bool Registered = false)
{
    public bool Installed => ExecutablePath is not null || UserProcessRunning || Registered;
}
public sealed record ScoutBackendStatus(bool Installed, bool Ready, bool Indexing = false, string? Error = null);
public interface IScoutEngine
{
    ScoutEngineInstallation Inspect();
    void Start(string executablePath);
}

public sealed class WindowsScoutEngine : IScoutEngine
{
    // Everything's service also runs as Everything.exe, but session zero cannot serve the user's SDK IPC.
    public static bool IsUserProcess(int processSession, int currentSession) => processSession != 0 && processSession == currentSession;
    public ScoutEngineInstallation Inspect() => OperatingSystem.IsWindows() ? InspectWindows() : new(null, false);
    [SupportedOSPlatform("windows")]
    private static ScoutEngineInstallation InspectWindows()
    {
        var candidates = new List<string>();
        var running = false; var registered = false;
        using var current = Process.GetCurrentProcess();
        foreach (var name in new[] { "Everything", "Everything64" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            try
            {
                if (!IsUserProcess(process.SessionId, current.SessionId)) continue;
                running = true;
                if (process.MainModule?.FileName is { } path) candidates.Add(path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            if (folder.Length > 0) candidates.Add(Path.Combine(folder, "Everything", "Everything.exe"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(profile, "scoop", "apps", "everything", "current", "Everything.exe"));
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var appPath = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
                if (appPath?.GetValue("") is string appExecutable) candidates.Add(appExecutable.Trim('"'));
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Everything");
                if (uninstall is null) continue;
                registered = true;
                if (uninstall.GetValue("InstallLocation") is string location && location.Length > 0) candidates.Add(Path.Combine(location, "Everything.exe"));
                if (uninstall.GetValue("UninstallString") is string command && ExecutableFromCommand(command) is { } executable)
                    candidates.Add(Path.Combine(Path.GetDirectoryName(executable) ?? "", "Everything.exe"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return new(candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists), running, registered);
    }
    public static string? ExecutableFromCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        var end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : command[..(end + 4)].TrimStart('"');
    }
    public void Start(string executablePath)
    {
        using var process = Process.Start(new ProcessStartInfo(executablePath) { Arguments = "-startup", UseShellExecute = false, CreateNoWindow = true });
    }
}
