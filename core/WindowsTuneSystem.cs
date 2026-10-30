using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>The real PC: registry values in the current user's and the machine's registry.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTuneSystem : ITuneSystem
{
    public string Edition { get; } = Version("EditionID");

    public int Build { get; } = int.TryParse(Version("CurrentBuild"), out var build) ? build : 0;

    private static string Version(string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue(name) as string ?? "";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return ""; }
    }

    public int Revision { get; } = ReadRevision();

    private static int ReadRevision()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("UBR") as int? ?? 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return 0; }
    }

    internal static RegistryKey Root(TuneScope scope) => RegistryKey.OpenBaseKey(scope == TuneScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    public TuneValue? Read(TuneScope scope, string key, string name)
    {
        try
        {
            using var root = Root(scope);
            using var subKey = root.OpenSubKey(key);
            if (subKey is null || !Has(subKey, name)) return null;
            var value = subKey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return subKey.GetValueKind(name) switch
            {
                RegistryValueKind.DWord when value is int number => new(TuneKind.Dword, ((uint)number).ToString()),
                RegistryValueKind.String or RegistryValueKind.ExpandString when value is string text => new(TuneKind.String, text),
                _ => new(TuneKind.Other, "")
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    // GetValue("") returns null for a (Default) value that was never set, so "set" has to be told apart from "not there".
    private static bool Has(RegistryKey key, string name) => name.Length == 0 ? key.GetValueNames().Contains("", StringComparer.Ordinal) : key.GetValue(name) is not null;
}

/// <summary>The only writes the Tune page makes: one registry value set or removed, and a key it created removed again. Each is checked against <see cref="TuneGuard"/> first.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTuneWriter : ITuneWriter
{
    public void Set(TuneScope scope, string key, string name, TuneKind kind, string text)
    {
        Check(scope, key, name);
        try
        {
            using var root = WindowsTuneSystem.Root(scope);
            using var subKey = root.CreateSubKey(key, writable: true);
            switch (kind)
            {
                case TuneKind.Dword when uint.TryParse(text, out var number): subKey.SetValue(name, unchecked((int)number), RegistryValueKind.DWord); break;
                case TuneKind.String: subKey.SetValue(name, text, RegistryValueKind.String); break;
                default: throw new InvalidOperationException("That setting cannot be changed.");
            }
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    public void Delete(TuneScope scope, string key, string name)
    {
        Check(scope, key, name);
        try
        {
            using var root = WindowsTuneSystem.Root(scope);
            using var subKey = root.OpenSubKey(key, writable: true);
            subKey?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    public void DeleteKey(TuneScope scope, string key)
    {
        if (!TuneGuard.AllowsRemoval(scope, key)) throw new InvalidOperationException("That setting cannot be changed.");
        try
        {
            using var root = WindowsTuneSystem.Root(scope);
            root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    private static void Check(TuneScope scope, string key, string name)
    {
        if (!TuneGuard.AllowsValue(scope, key, name)) throw new InvalidOperationException("That setting cannot be changed.");
    }
}

/// <summary>Restarts File Explorer for the current user, so shell settings take effect without signing out.</summary>
[SupportedOSPlatform("windows")]
public static class ExplorerShell
{
    public static async Task RestartAsync()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (process.SessionId != session) continue;
                try { process.Kill(); process.WaitForExit(5000); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        // Windows starts the shell again by itself; if it has not within a few seconds, start it.
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (Process.GetProcessesByName("explorer").Any(x => { using (x) return x.SessionId == session; })) return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }
}
