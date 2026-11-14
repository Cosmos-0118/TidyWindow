using System.Text.Json.Nodes;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The terminal setup rewrites files the person may have spent years on, so what matters is that Restore puts every one back exactly.</summary>
public sealed class TerminalSetupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-terminal-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TerminalPaths paths;
    private string Settings => paths.TerminalSettings[0];

    public TerminalSetupTests()
    {
        paths = new(Path.Combine(root, "Documents", "PowerShell", "Microsoft.PowerShell_profile.ps1"), Path.Combine(root, ".config", "starship.toml"),
            [Path.Combine(root, "wt", "LocalState", "settings.json")], [Path.Combine(root, "Modules", "PSFzf")], Path.Combine(root, "Backups"));
        Directory.CreateDirectory(Path.GetDirectoryName(Settings)!);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private TerminalSetup Setup(bool pwsh = true) => new(paths, name => name != "pwsh" || pwsh, () => true);

    private void Existing(string profile, string settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Profile)!);
        File.WriteAllText(paths.Profile, profile);
        File.WriteAllText(Settings, settings);
    }

    [Fact]
    public void Applying_writes_the_three_parts_and_reports_them_as_applied()
    {
        var setup = Setup();
        Assert.False(setup.Inspect().LookApplied);
        var result = setup.Apply();
        Assert.True(result.Ok, result.Detail);
        var status = setup.Inspect();
        Assert.True(status.ProfileApplied && status.StarshipApplied && status.TerminalApplied && status.LookApplied);
        Assert.True(File.Exists(paths.Starship));
    }

    [Fact]
    public void Restore_brings_back_the_old_files_and_removes_ones_that_did_not_exist()
    {
        Existing("# my own profile\r\n", "{\r\n  // keep me\r\n  \"theme\": \"dark\"\r\n}\r\n");
        var profile = File.ReadAllBytes(paths.Profile); var settings = File.ReadAllBytes(Settings);
        var setup = Setup();
        Assert.True(setup.Apply().Ok);
        Assert.NotEqual(profile, File.ReadAllBytes(paths.Profile));
        Assert.True(setup.Inspect().CanRestore);

        Assert.True(setup.Restore().Ok);
        Assert.Equal(profile, File.ReadAllBytes(paths.Profile));
        Assert.Equal(settings, File.ReadAllBytes(Settings));
        Assert.False(File.Exists(paths.Starship));
        Assert.False(setup.Inspect().CanRestore);
        Assert.False(setup.Inspect().LookApplied);
    }

    [Fact]
    public void Existing_terminal_settings_survive_the_merge()
    {
        Existing("", """
          { // comment
          "defaultProfile": "{other}", "copyOnSelect": true, "keybindings": [ { "id": "x", "keys": "ctrl+shift+q" } ],
          "profiles": { "defaults": { "fontSize": 99, "opacity": 10 }, "list": [ { "name": "Mine", "guid": "{1}" } ] },
          "schemes": [ { "name": "Mine", "background": "#000000" }, { "name": "Catppuccin Mocha", "background": "#FFFFFF" } ] }
          """);
        Assert.True(Setup().Apply().Ok);
        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.True(root["copyOnSelect"]!.GetValue<bool>());
        Assert.Equal("ctrl+shift+q", root["keybindings"]![0]!["keys"]!.GetValue<string>());
        Assert.Equal("Mine", root["profiles"]!["list"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(99, root["profiles"]!["defaults"]!["fontSize"]!.GetValue<int>());
        Assert.Equal(92, root["profiles"]!["defaults"]!["opacity"]!.GetValue<int>());
        Assert.Equal(TerminalKit.PowerShell7ProfileId, root["defaultProfile"]!.GetValue<string>());
        var schemes = root["schemes"]!.AsArray();
        Assert.Equal(2, schemes.Count);
        Assert.Equal("#1E1E2E", schemes.Single(x => x!["name"]!.GetValue<string>() == "Catppuccin Mocha")!["background"]!.GetValue<string>());
    }

    [Fact]
    public void Without_PowerShell_7_the_default_profile_is_left_alone()
    {
        Existing("", "{ \"defaultProfile\": \"{other}\" }");
        Assert.True(Setup(pwsh: false).Apply().Ok);
        Assert.Equal("{other}", JsonNode.Parse(File.ReadAllText(Settings))!["defaultProfile"]!.GetValue<string>());
    }

    private JsonNode Written() => JsonNode.Parse(File.ReadAllText(Settings))!;
    private static bool HasPowerShell7(JsonNode root) => root["profiles"]!["list"]!.AsArray().Any(x => string.Equals(x!["guid"]!.GetValue<string>(), TerminalKit.PowerShell7ProfileId, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_PowerShell_7_profile_is_added_before_it_becomes_the_default()
    {
        Existing("", "{ \"profiles\": { \"list\": [ { \"name\": \"Mine\", \"guid\": \"{1}\" } ] } }");
        Assert.True(Setup().Apply().Ok);
        var root = Written();
        Assert.Equal(TerminalKit.PowerShell7ProfileId, root["defaultProfile"]!.GetValue<string>());
        var added = root["profiles"]!["list"]!.AsArray().Single(x => x!["guid"]!.GetValue<string>() == TerminalKit.PowerShell7ProfileId)!;
        Assert.Equal("pwsh.exe", added["commandline"]!.GetValue<string>());
        Assert.Equal("{1}", root["profiles"]!["list"]![0]!["guid"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"profiles\": [ { \"name\": \"Mine\", \"guid\": \"{1}\" } ] }")]
    public void A_missing_profile_list_is_created_for_the_default_profile(string settings)
    {
        Existing("", settings);
        Assert.True(Setup().Apply().Ok);
        Assert.True(HasPowerShell7(Written()));
    }

    [Fact]
    public void A_PowerShell_7_profile_that_is_already_there_is_kept_as_it_is()
    {
        Existing("", "{ \"profiles\": { \"list\": [ { \"guid\": \"{574E775E-4F2A-5B96-AC1E-A2962A402336}\", \"name\": \"My shell\", \"commandline\": \"C:\\\\tools\\\\pwsh.exe\" } ] } } ");
        Assert.True(Setup().Apply().Ok);
        var list = Written()["profiles"]!["list"]!.AsArray();
        Assert.Single(list);
        Assert.Equal("My shell", list[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Without_PowerShell_7_no_profile_is_added_either()
    {
        Existing("", "{ \"defaultProfile\": \"{other}\" }");
        Assert.True(Setup(pwsh: false).Apply().Ok);
        Assert.Null(Written()["profiles"]!["list"]);
    }

    [Fact]
    public void A_profile_list_that_is_not_a_list_stops_the_setup_before_anything_is_written()
    {
        Existing("# mine", "{ \"profiles\": { \"list\": \"oops\" } }");
        var result = Setup().Apply();
        Assert.False(result.Ok);
        Assert.Contains(Settings, result.Detail);
        Assert.Equal("# mine", File.ReadAllText(paths.Profile));
        Assert.False(File.Exists(paths.Starship));
    }

    [Fact]
    public void Settings_written_by_an_earlier_version_with_a_missing_default_profile_are_not_reported_as_applied()
    {
        var setup = Setup();
        Assert.True(setup.Apply().Ok);
        Assert.True(setup.Inspect().LookApplied);
        // What earlier versions left behind: the theme is there, the default profile points at a profile that is not in the list.
        var root = Written();
        root["profiles"]!["list"] = new JsonArray();
        File.WriteAllText(Settings, root.ToJsonString());
        Assert.False(setup.Inspect().LookApplied);
        Assert.True(setup.Apply().Ok);
        Assert.True(HasPowerShell7(Written()));
        Assert.True(setup.Inspect().LookApplied);
    }

    [Fact]
    public void Applying_twice_changes_nothing_the_second_time()
    {
        var setup = Setup();
        Assert.True(setup.Apply().Ok);
        var backups = Directory.GetDirectories(paths.Backups).Length;
        var result = setup.Apply();
        Assert.True(result.Ok);
        Assert.Contains("already", result.Message);
        Assert.Equal(backups, Directory.GetDirectories(paths.Backups).Length);
    }

    [Fact]
    public void Unreadable_terminal_settings_stop_the_setup_before_anything_is_written()
    {
        Existing("# mine", "{ not json");
        var result = Setup().Apply();
        Assert.False(result.Ok);
        Assert.Equal("# mine", File.ReadAllText(paths.Profile));
        Assert.False(File.Exists(paths.Starship));
    }

    [Fact]
    public void A_failed_write_puts_back_what_was_already_written()
    {
        Existing("# mine", "{}");
        // The settings file cannot be replaced while it is open, which is what an editor holding it would do.
        using var held = new FileStream(Settings, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = Setup().Apply();
        Assert.False(result.Ok);
        held.Dispose();
        Assert.Equal("# mine", File.ReadAllText(paths.Profile));
        Assert.False(File.Exists(paths.Starship));
    }

    [Fact]
    public void Only_the_latest_apply_is_undone_each_time()
    {
        Existing("# one", "{}");
        var setup = Setup();
        Assert.True(setup.Apply().Ok);
        File.WriteAllText(paths.Profile, "# two");
        Assert.True(setup.Apply().Ok);
        Assert.True(setup.Restore().Ok);
        Assert.Equal("# two", File.ReadAllText(paths.Profile));
        Assert.True(setup.Restore().Ok);
        Assert.Equal("# one", File.ReadAllText(paths.Profile));
        Assert.False(setup.Restore().Ok);
    }

    [Fact]
    public void Missing_tools_are_listed_and_the_embedded_files_are_present()
    {
        var setup = new TerminalSetup(paths, name => name is not ("fzf" or "eza"), () => false);
        var status = setup.Inspect();
        Assert.Equal(["fzf", "eza"], status.Missing.Select(x => x.Command).OrderByDescending(x => x == "fzf"));
        Assert.False(status.Font);
        Assert.All(TerminalKit.Tools, x => Assert.Equal(ManagerKind.Winget, x.ToPackage().Manager));
        Assert.Contains("starship", TerminalKit.Profile);
        Assert.Contains("palette", TerminalKit.Starship);
    }

    [Fact]
    public void Only_apps_the_setup_added_are_offered_for_removal_and_failed_installs_are_forgotten()
    {
        var present = new HashSet<string> { "git", "pwsh" };
        var setup = new TerminalSetup(paths, present.Contains, () => true);
        var wanted = TerminalKit.Tools.Where(x => x.Command is "fzf" or "eza" or "bat").ToList();
        setup.RememberInstalled(wanted);

        // Still waiting in the queue: kept, but nothing to remove yet.
        var waiting = setup.Inspect(id => true);
        Assert.Empty(waiting.Added);

        present.Add("fzf"); present.Add("eza");   // bat's install failed
        var done = setup.Inspect();
        Assert.Equal(["eza", "fzf"], done.Added.Select(x => x.Command).Order());
        Assert.DoesNotContain(done.Added, x => x.Command is "git" or "pwsh" or "bat");

        // Removed by hand and put back later by something else: it must not be offered as ours.
        present.Remove("eza");
        setup.Inspect();
        present.Add("eza");
        Assert.Equal(["fzf"], setup.Inspect().Added.Select(x => x.Command));
    }

    [Fact]
    public void The_font_is_offered_for_removal_only_when_the_setup_installed_it_and_it_is_still_there()
    {
        var present = true;
        var setup = new TerminalSetup(paths, _ => true, () => present);
        Assert.False(setup.Inspect().FontAdded);   // already on the PC: never ours
        setup.RememberFont(true);
        Assert.True(setup.Inspect().FontAdded);
        present = false;                            // removed by hand: forgotten
        Assert.False(setup.Inspect().FontAdded);
        present = true;
        Assert.False(setup.Inspect().FontAdded);
    }

    [Fact]
    public async Task The_module_is_remembered_after_installing_and_forgotten_after_removing()
    {
        Directory.CreateDirectory(Path.Combine(root, "pwsh"));
        var fake = Path.Combine(root, "pwsh", "pwsh.exe");
        File.WriteAllText(fake, "");
        var oldPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(fake) + ";" + oldPath);
        try
        {
            var setup = Setup();
            var module = paths.PsFzfFolders[0];
            Assert.False(setup.Inspect().ModuleAdded);
            var installed = await setup.InstallPsFzfAsync(new FakeRunner(() => Directory.CreateDirectory(module)), null, default);
            Assert.True(installed.Ok, installed.Detail);
            Assert.True(setup.Inspect().ModuleAdded);
            var removed = await setup.UninstallPsFzfAsync(new FakeRunner(() => Directory.Delete(module, true)), null, default);
            Assert.True(removed.Ok, removed.Detail);
            Assert.False(setup.Inspect().ModuleAdded);
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath, EnvironmentVariableTarget.Process); }
    }

    [Fact]
    public void The_profile_keeps_Starship_warnings_out_of_the_prompt_in_big_folders()
    {
        // The Starship config scans folders for only a few milliseconds, so a folder like System32 times out and Starship would print a warning above every prompt.
        Assert.Contains("scan_timeout", TerminalKit.Starship);
        Assert.Matches(@"\$env:STARSHIP_LOG\s*=\s*'error'", TerminalKit.Profile);
    }

    private sealed class FakeRunner(Action effect) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) { effect(); return Task.FromResult(new CommandResult(0, "", "")); }
    }
}
