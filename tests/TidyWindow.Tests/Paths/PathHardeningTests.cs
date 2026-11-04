using System.Buffers.Binary;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>A failed PATH write leaves no bogus Undo, a typed folder is stored as typed, and asking a program its version never opens a window.</summary>
public sealed class PathHardeningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-pathh-" + Guid.NewGuid().ToString("N")[..8]);
    private int broadcasts;

    public PathHardeningTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Folder(string name, bool create = true)
    {
        var path = Path.Combine(root, name);
        if (create) Directory.CreateDirectory(path);
        return path;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class Store : IPathStore
    {
        public string? Machine, User;
        public Exception? Fails;
        public string? Read(PathScope scope) => scope == PathScope.Machine ? Machine : User;
        public string? Variable(PathScope scope, string name) => null;
        public void Write(PathScope scope, string value, string? previous)
        {
            if (Fails is not null) throw Fails;
            if (scope == PathScope.Machine) Machine = value; else User = value;
        }
    }

    private PathDoctor Doctor(Store store) =>
        new(store, new PathBackup(Path.Combine(root, "_backups")), new PathAnalyzer(x => x.Replace("%ROOT%", root, StringComparison.OrdinalIgnoreCase), Directory.Exists), () => broadcasts++);

    [Fact]
    public void AChangeThatCouldNotBeSavedLeavesNothingToUndo()
    {
        var before = $"{Folder("a")};{Folder("gone", create: false)}";
        var store = new Store { User = before, Fails = new IOException("The administrator helper stopped.") };
        var doctor = Doctor(store);

        Assert.Throws<IOException>(() => doctor.FixProblems());

        Assert.Null(doctor.LastBackup);
        Assert.False(doctor.Undo().Changed);
        Assert.Equal(0, broadcasts);
        Assert.Equal(before, store.User);
    }

    [Fact]
    public void AFolderGivenWithAVariableIsStoredAsGivenAndJudgedExpanded()
    {
        var tools = Folder("tools");
        var store = new Store { User = "" };
        var doctor = Doctor(store);

        Assert.True(doctor.Add(@"%ROOT%\tools\").Changed);
        Assert.Equal(@"%ROOT%\tools", store.User);
        Assert.Contains("already", doctor.Add(tools).Message);
        Assert.Contains("does not exist", doctor.Add(@"%NOPE%\bin").Message);
        Assert.Contains("whole path", doctor.Add("C:").Message);
        Assert.Contains("whole path", doctor.Add(@"bin\tools").Message);
        Assert.False(doctor.AddMany(["C:", @"%NOPE%\bin"]).Changed);
        Assert.Equal(@"%ROOT%\tools", store.User);
    }

    /// <summary>A program file with nothing but the headers: an MZ stub pointing at a PE signature, a file header, and an optional header naming the subsystem.</summary>
    private static byte[] Pe(ushort subsystem)
    {
        var bytes = new byte[64 + 94 + 8];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(64 + 24), 0x20B);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(64 + 92), subsystem);
        return bytes;
    }

    [Fact]
    public void AProgramsKindIsReadFromItsHeaderNotItsName()
    {
        Assert.Equal(ProgramKind.Console, ProgramFile.Kind(Write("console.exe", Pe(3))));
        Assert.Equal(ProgramKind.Windowed, ProgramFile.Kind(Write("app.exe", Pe(2))));
        Assert.Equal(ProgramKind.Unknown, ProgramFile.Kind(Write("notes.exe", "hello"u8.ToArray())));
        Assert.Equal(ProgramKind.Unknown, ProgramFile.Kind(Write("short.exe", [(byte)'M', (byte)'Z'])));
        Assert.Equal(ProgramKind.Script, ProgramFile.Kind(Write("tool.cmd", "@echo off"u8.ToArray())));
        Assert.Equal(ProgramKind.Unknown, ProgramFile.Kind(Path.Combine(root, "missing.exe")));
    }

    [Fact]
    public async Task AWindowedProgramIsNotStartedToAskItsVersion()
    {
        var windowed = Write("app.exe", Pe(2));

        var result = await CommandProbe.RunAsync("app", windowed);

        Assert.True(result.Skipped);
        Assert.Equal("", result.Version);
        // A Store alias cannot be read, so it is run; this one cannot start, which is reported as no version rather than as skipped.
        var alias = await CommandProbe.RunAsync("app", windowed, alias: true);
        Assert.False(alias.Skipped);
        Assert.Equal("", alias.Version);
    }

    [Fact]
    public async Task ATamperedBackupCannotPutAFolderAnyoneCanWriteToOnTheWindowsWidePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var machine = @"C:\Windows";
        using var session = new MachinePathSession(pipe => MachinePathHost.RunAsync([MachinePathHost.Command, pipe], () => machine, value => machine = value, () => true, TimeSpan.FromSeconds(20)));
        await session.OpenAsync();
        // The app's backup file can be edited by any program running as the ordinary account; this one names a folder inside the profile.
        var backups = new PathBackup(Path.Combine(root, "machine"));
        backups.Record($@"C:\Windows;{root}", "Removed " + root);
        var doctor = new PathDoctor(new ElevatedPathStore(new Store { Machine = machine }, session), backups, new PathAnalyzer(x => x, Directory.Exists), () => broadcasts++, PathScope.Machine);

        var refused = Assert.Throws<IOException>(() => doctor.Undo());

        Assert.Contains("administrator", refused.Message);
        Assert.Equal(@"C:\Windows", machine);
        Assert.Equal(0, broadcasts);
    }

    [Fact]
    public void BackupsMadeInTheSameMillisecondKeepTheirOwnCopy()
    {
        var backup = new PathBackup(Path.Combine(root, "_same-ms"));
        var ids = Enumerable.Range(0, 40).Select(n => backup.Record("value " + n, "test").Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(30, Directory.EnumerateFiles(Path.Combine(root, "_same-ms"), "*.json").Count());
        Assert.Equal("value 39", backup.Latest()!.Raw);
    }
}
