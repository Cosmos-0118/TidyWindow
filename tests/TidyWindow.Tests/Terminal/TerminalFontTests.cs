using System.IO.Compression;
using System.Security.Cryptography;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The font is installed machine-wide by an administrator helper, so what matters is that it only ever installs the reviewed package, and nothing else.</summary>
public sealed class TerminalFontTests
{
    private static byte[] Face(string name) => System.Text.Encoding.UTF8.GetBytes("font data for " + name);

    private static byte[] Archive(params string[] left)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var name in TerminalKit.FontFiles.Select(x => x.File).Concat(["CaskaydiaCoveNerdFontMono-Light.ttf", "readme.md", "../escape.ttf"]).Except(left))
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Face(name));
            }
        }
        return memory.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class FakeStore : IFontStore
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public Dictionary<string, string> Registered { get; } = [];
        public string? FailOn { get; set; }
        public int Announced { get; private set; }
        public bool Matches(string file, byte[] content) => Files.TryGetValue(file, out var have) && have.AsSpan().SequenceEqual(content);
        public void Write(string file, byte[] content) { if (file == FailOn) throw new IOException("in use"); Files[file] = content; }
        public void Register(string registration, string file) => Registered[registration] = file;
        public void Remove(string registration, string file)
        {
            if (file == FailOn) throw new IOException("in use");
            Files.Remove(file); Registered.Remove(registration);
        }
        public void Announce() => Announced++;
    }

    private static FontInstaller Installer(byte[] archive, FakeStore store, string? hash = null) => new((_, _) => Task.FromResult(archive), store, hash ?? Hash(archive));

    [Fact]
    public async Task Installs_exactly_the_four_faces_under_names_it_chose()
    {
        var store = new FakeStore();
        var result = await Installer(Archive(), store).InstallAsync(default);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(TerminalKit.FontFiles.Select(x => x.File).Order(), store.Files.Keys.Order());
        Assert.All(TerminalKit.FontFiles, x => Assert.Equal(x.File, store.Registered[x.Registration]));
        Assert.Equal(Face(TerminalKit.FontFiles[0].File), store.Files[TerminalKit.FontFiles[0].File]);
        Assert.Equal(1, store.Announced);
    }

    [Fact]
    public async Task A_package_that_does_not_match_the_checksum_installs_nothing()
    {
        var store = new FakeStore();
        var result = await Installer(Archive(), store, new string('0', 64)).InstallAsync(default);
        Assert.False(result.Ok);
        Assert.Contains("checksum", result.Message);
        Assert.Empty(store.Files);
        Assert.Empty(store.Registered);
    }

    [Fact]
    public async Task A_package_missing_a_face_installs_nothing()
    {
        var store = new FakeStore();
        var result = await Installer(Archive(TerminalKit.FontFiles[3].File), store).InstallAsync(default);
        Assert.False(result.Ok);
        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task A_failed_write_takes_back_what_this_run_added_but_not_what_was_already_there()
    {
        var store = new FakeStore { FailOn = TerminalKit.FontFiles[2].File };
        var already = TerminalKit.FontFiles[0];
        store.Files[already.File] = Face(already.File);
        var result = await Installer(Archive(), store).InstallAsync(default);
        Assert.False(result.Ok);
        Assert.Equal([already.File], store.Files.Keys);
        Assert.Contains("in use", result.Detail);
    }

    [Fact]
    public async Task A_download_that_fails_or_is_stopped_installs_nothing()
    {
        var store = new FakeStore();
        var failed = await new FontInstaller((_, _) => throw new HttpRequestException("offline"), store).InstallAsync(default);
        Assert.False(failed.Ok);
        Assert.Contains("connection", failed.Message);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var stopped = await new FontInstaller((_, token) => Task.FromCanceled<byte[]>(token), store).InstallAsync(cancel.Token);
        Assert.False(stopped.Ok);
        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task Removing_takes_away_only_the_four_faces_and_says_when_one_is_in_use()
    {
        var store = new FakeStore();
        var installer = Installer(Archive(), store);
        await installer.InstallAsync(default);
        store.Files["SomethingElse.ttf"] = [1];
        Assert.True(installer.Remove().Ok);
        Assert.Equal(["SomethingElse.ttf"], store.Files.Keys);

        await installer.InstallAsync(default);
        store.FailOn = TerminalKit.FontFiles[1].File;
        var stuck = installer.Remove();
        Assert.False(stuck.Ok);
        Assert.Contains(TerminalKit.FontFiles[1].File, stuck.Detail);
    }

    [Fact]
    public async Task The_helper_answers_the_app_over_the_pipe_and_ignores_anything_but_install_and_remove()
    {
        var store = new FakeStore();
        var archive = Archive();
        var installer = Installer(archive, store);
        var session = new FontSession((pipe, op) => FontHost.RunAsync([FontHost.Command, pipe, op], installer));
        var allowed = false;
        var steps = new List<FontProgress>();
        var installed = await session.InstallAsync(() => allowed = true, new Sync(steps.Add));
        Assert.True(installed.Ok, installed.Message + installed.Detail);
        Assert.True(allowed);
        Assert.Equal(4, store.Files.Count);
        Assert.Equal(0, steps[0].Percent);
        Assert.Equal(100, steps[^1].Percent);
        Assert.Equal(steps.Select(x => x.Percent).Order(), steps.Select(x => x.Percent));   // the bar only moves forward
        Assert.True((await session.RemoveAsync()).Ok);
        Assert.Empty(store.Files);

        Assert.Equal(64, await FontHost.RunAsync([FontHost.Command, "pipe", "C:\\evil.ttf"], installer));
        Assert.Equal(64, await FontHost.RunAsync([FontHost.Command, "pipe"], installer));
    }

    [Fact]
    public async Task The_download_fills_most_of_the_bar_and_reports_megabytes()
    {
        var archive = Archive();
        var steps = new List<FontProgress>();
        var installer = new FontInstaller((report, _) => { report(1, 4); report(2, 4); report(4, 4); return Task.FromResult(archive); }, new FakeStore(), Hash(archive));
        Assert.True((await installer.InstallAsync(default, new Sync(steps.Add))).Ok);
        Assert.Equal([0, 22, 45, 90], steps.Take(4).Select(x => x.Percent));
        Assert.All(steps.Skip(1).Take(3), x => Assert.StartsWith("Downloading", x.Step));
        Assert.Equal(92, steps[4].Percent);
        Assert.All(steps, x => Assert.InRange(x.Percent!.Value, 0, 100));
    }

    [Fact]
    public async Task Removing_reports_progress_without_a_percentage()
    {
        var steps = new List<FontProgress>();
        var session = new FontSession((pipe, op) => FontHost.RunAsync([FontHost.Command, pipe, op], Installer(Archive(), new FakeStore())));
        Assert.True((await session.RemoveAsync(null, new Sync(steps.Add))).Ok);
        Assert.Contains(steps, x => x.Percent is null);
    }

    private sealed class Sync(Action<FontProgress> action) : IProgress<FontProgress> { public void Report(FontProgress value) => action(value); }

    [Fact]
    public async Task A_declined_permission_prompt_is_reported_and_nothing_is_installed()
    {
        var session = new FontSession((_, _) => Task.FromException(new OperationCanceledException("declined")));
        var result = await session.InstallAsync();
        Assert.False(result.Ok);
        Assert.Contains("declined", result.Message);
    }

    [Fact]
    public async Task A_helper_that_cannot_start_is_reported()
    {
        var session = new FontSession((_, _) => Task.FromException(new IOException("The administrator helper is missing.")));
        var result = await session.InstallAsync();
        Assert.False(result.Ok);
        Assert.Contains("missing", result.Detail);
    }

    [Fact]
    public void The_pinned_package_is_a_https_release_with_a_full_checksum()
    {
        Assert.StartsWith("https://", TerminalKit.FontUrl);
        Assert.Matches("^[0-9a-f]{64}$", TerminalKit.FontSha256);
        Assert.All(TerminalKit.FontFiles, x => { Assert.Equal(Path.GetFileName(x.File), x.File); Assert.Contains("CaskaydiaCove NFM", x.Registration); });
    }
}
