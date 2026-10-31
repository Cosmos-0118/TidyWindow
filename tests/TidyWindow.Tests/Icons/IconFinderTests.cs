using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

internal sealed class FakeIconPc : IIconSystem
{
    public HashSet<string> Present = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> OnPath = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Registered = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (string Target, string Icon)> Links = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProductIcons = new(StringComparer.OrdinalIgnoreCase);
    public List<UninstallIconFact> Installed = [];
    public List<string> Shortcuts = [];

    private static readonly (string Variable, string Value)[] Variables =
    [
        ("%ProgramFiles%", @"C:\Program Files"), ("%ProgramFiles(x86)%", @"C:\Program Files (x86)"), ("%SystemRoot%", @"C:\Windows"),
        ("%LocalAppData%", @"C:\Users\me\AppData\Local"), ("%AppData%", @"C:\Users\me\AppData\Roaming"), ("%UserProfile%", @"C:\Users\me")
    ];

    public string Expand(string text) => Variables.Aggregate(text, (current, v) => current.Replace(v.Variable, v.Value, StringComparison.OrdinalIgnoreCase));
    public bool FileExists(string path) => Present.Contains(path);
    public string? FindOnPath(string fileName) => OnPath.GetValueOrDefault(fileName);
    public string? AppPath(string fileName) => Registered.GetValueOrDefault(fileName);
    public (string Target, string Icon) Shortcut(string path) => Links.GetValueOrDefault(path);
    public IReadOnlyList<string> Subfolders(string folder) => Present.Select(Path.GetDirectoryName).Where(x => x is not null && string.Equals(Path.GetDirectoryName(x), folder, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()!;
    public IReadOnlyList<string> Files(string folder, string pattern)
    {
        var wildcard = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return Present.Where(x => string.Equals(Path.GetDirectoryName(x), folder, StringComparison.OrdinalIgnoreCase) && wildcard.IsMatch(Path.GetFileName(x))).ToList();
    }
    public string? ReadText(string path) => Texts.GetValueOrDefault(path);
    public IReadOnlyList<UninstallIconFact> Programs() => Installed;
    public IReadOnlyList<string> StartMenu() => Shortcuts;
    public string ProductIcon(string productCode) => ProductIcons.GetValueOrDefault(productCode) ?? "";

    public FakeIconPc Has(params string[] paths) { foreach (var path in paths) Present.Add(path); return this; }
}

public sealed class IconCommandTests
{
    private static string Expand(string text) => text.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase).Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase);
    private static string[] Paths(string command) => IconCommand.Targets(command, Expand).Select(x => x.Path).ToArray();

    [Theory]
    [InlineData(@"""C:\Program Files\Acme\acme.exe"" --tray", @"C:\Program Files\Acme\acme.exe", "--tray")]
    [InlineData(@"C:\Program Files\Acme App\acme.exe /minimized", @"C:\Program Files\Acme App\acme.exe", "/minimized")]
    [InlineData(@"C:\Tools\run.bat", @"C:\Tools\run.bat", "")]
    [InlineData(@"acme", "acme", "")]
    [InlineData(@"acme -x 1", "acme", "-x 1")]
    [InlineData(@"""C:\Acme\a.exe""", @"C:\Acme\a.exe", "")]
    public void ACommandLineIsSplitIntoTheProgramAndWhatFollows(string command, string program, string arguments)
    {
        var (found, rest) = IconCommand.Split(command);
        Assert.Equal(program, found);
        Assert.Equal(arguments, rest);
    }

    [Fact]
    public void EnvironmentVariablesAreExpandedInTheProgramPath() =>
        Assert.Equal([@"C:\Program Files\Acme\acme.exe"], Paths(@"""%ProgramFiles%\Acme\acme.exe"" -silent"));

    [Fact]
    public void CmdStartsWhateverItsCommandNames()
    {
        Assert.Equal([@"C:\Acme\acme.exe"], Paths(@"cmd.exe /c start """" ""C:\Acme\acme.exe"" --hidden"));
        Assert.Equal([@"C:\Acme\acme.exe"], Paths(@"%SystemRoot%\System32\cmd.exe /c ""C:\Acme\acme.exe"""));
        Assert.Equal([@"C:\Acme\acme.exe"], Paths(@"cmd /k start /min ""C:\Acme\acme.exe"""));
    }

    [Fact]
    public void ABareNameAfterCdIsTriedInThatFolderFirst() =>
        Assert.Equal([@"D:\Apps\acme.exe", "acme.exe"], Paths(@"cmd /c ""cd /d D:\Apps && start acme.exe --tray"""));

    [Fact]
    public void ARundll32LibraryIsTheTargetButASystemLibraryIsNot()
    {
        Assert.Equal([@"C:\Acme\acme.dll"], Paths(@"rundll32.exe ""C:\Acme\acme.dll"",Start"));
        Assert.Equal([@"C:\Acme\acme.dll"], Paths(@"rundll32.exe C:\Acme\acme.dll,Start now"));
        Assert.Empty(Paths(@"rundll32.exe shell32.dll,Control_RunDLL"));
    }

    [Fact]
    public void ScriptHostsAreLookedThroughForTheProgramTheyStart()
    {
        Assert.Equal([@"C:\Acme\acme.exe"], Paths(@"powershell.exe -WindowStyle Hidden -Command ""& 'C:\Acme\acme.exe' --tray"""));
        Assert.Equal([@"C:\Acme\acme.exe", @"C:\Acme\start.ps1"], Paths(@"pwsh -File C:\Acme\start.ps1 ""C:\Acme\acme.exe"""));
        Assert.Equal([@"C:\Program Files\Acme\acme.exe"], Paths(@"wscript.exe //B ""%ProgramFiles%\Acme\acme.exe"""));
    }

    [Fact]
    public void ExplorerOpeningAFileHasTheFileFirstAndItselfAfter() =>
        Assert.Equal([@"C:\Acme\acme.lnk", @"C:\Windows\explorer.exe"], Paths(@"%SystemRoot%\explorer.exe ""C:\Acme\acme.lnk"""));

    [Fact]
    public void LaunchersWithNothingInsideLeaveNothing()
    {
        Assert.Empty(Paths(@"msiexec.exe /i {AAAA}"));
        Assert.Empty(Paths(""));
    }

    [Fact]
    public void SquirrelArgumentsStayWithTheProgram() =>
        Assert.Equal("--processStart Discord.exe", Assert.Single(IconCommand.Targets(@"""C:\Users\me\AppData\Local\Discord\Update.exe"" --processStart Discord.exe", Expand)).Arguments);

    [Theory]
    [InlineData(@"""C:\App\app.exe"",1", @"C:\App\app.exe", 1)]
    [InlineData(@"C:\App\app.exe,-101", @"C:\App\app.exe", -101)]
    [InlineData(@"C:\App\app.exe", @"C:\App\app.exe", 0)]
    [InlineData(@"C:\App\app.ico", @"C:\App\app.ico", 0)]
    [InlineData(@"""C:\App\app.exe""", @"C:\App\app.exe", 0)]
    [InlineData(@"C:\Odd, Folder\app.exe,2", @"C:\Odd, Folder\app.exe", 2)]
    [InlineData(@"C:\Odd, Folder\app.exe", @"C:\Odd, Folder\app.exe", 0)]
    public void AnIconLocationNamesTheFileAndWhichIcon(string text, string path, int index) => Assert.Equal((path, index), IconCommand.Location(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"""unterminated")]
    public void NothingUsableInAnIconLocationIsNull(string text) => Assert.Null(IconCommand.Location(text));
}

public sealed class IconFinderTests
{
    private const string Exe = @"C:\Program Files\Acme\acme.exe";

    private static IReadOnlyList<IconRef> Find(FakeIconPc pc, IconQuery query, bool dark = true) => new IconFinder(pc, () => dark).Find(query);
    private static IconRef[] Real(IReadOnlyList<IconRef> icons) => icons.Where(x => x.Source != IconSource.FileType).ToArray();

    [Fact]
    public void TheProgramTheCommandStartsComesFirstAndTheFileTypeIsTheLastResort()
    {
        var pc = new FakeIconPc().Has(Exe);
        var icons = Find(pc, new("Acme", $@"""{Exe}"" --tray", Exe));
        Assert.Equal([new IconRef(Exe, 0, IconSource.Program), new IconRef(Exe, 0, IconSource.FileType)], icons);
        Assert.True(IconFinder.IsSpecific(icons));
    }

    [Fact]
    public void WhatTheProgramRegisteredIsOfferedAfterItsOwnIconAndBeforeTheFileType()
    {
        var pc = new FakeIconPc().Has(Exe, @"C:\Program Files\Acme\acme.ico");
        pc.Installed.Add(new("Acme", @"C:\Program Files\Acme\acme.ico", @"C:\Program Files\Acme"));
        var icons = Find(pc, new("Acme", "", Exe));
        Assert.Equal([IconSource.Program, IconSource.DisplayIcon, IconSource.FileType], icons.Select(x => x.Source));
        Assert.Equal(@"C:\Program Files\Acme\acme.ico", icons[1].Path);
    }

    [Fact]
    public void AnEntrysOwnDisplayIconBeatsEverythingAndKeepsItsIndex()
    {
        var pc = new FakeIconPc().Has(Exe, @"C:\Program Files\Acme\tools.dll");
        var icons = Find(pc, new("Acme", "", "", @"""C:\Program Files\Acme\tools.dll"",-12", @"C:\Program Files\Acme"));
        Assert.Equal(new IconRef(@"C:\Program Files\Acme\tools.dll", -12, IconSource.DisplayIcon), icons[0]);
        Assert.Equal(new IconRef(Exe, 0, IconSource.Folder), icons[1]);
    }

    [Fact]
    public void ACommandWithNothingFoundGivesNoIconsAtAllSoThePageShowsItsGlyph()
    {
        var pc = new FakeIconPc();
        Assert.Empty(Find(pc, new("Mystery", "", "")));
        Assert.Empty(Find(pc, new("Mystery", @"cmd /c echo hi", @"C:\Windows\System32\cmd.exe")));
        Assert.False(IconFinder.IsSpecific([]));
    }

    [Fact]
    public void AMissingProgramStillGetsTheFileTypeIcon()
    {
        var icons = Find(new FakeIconPc(), new("Gone", @"""C:\Gone\gone.exe""", @"C:\Gone\gone.exe"));
        Assert.Equal([new IconRef(@"C:\Gone\gone.exe", 0, IconSource.FileType)], icons);
        Assert.False(IconFinder.IsSpecific(icons));
    }

    [Fact]
    public void AWrapperIsLookedThroughToTheRealProgram()
    {
        var pc = new FakeIconPc().Has(Exe, @"C:\Windows\System32\cmd.exe");
        var icons = Find(pc, new("Acme", $@"C:\Windows\System32\cmd.exe /c start """" ""{Exe}""", @"C:\Windows\System32\cmd.exe"));
        Assert.Equal(new IconRef(Exe, 0, IconSource.Program), icons[0]);
        Assert.DoesNotContain(icons, x => x.Path.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AShortcutGivesItsOwnIconAndThenWhatItPointsAt()
    {
        var lnk = @"C:\Users\me\Start\Acme.lnk";
        var pc = new FakeIconPc().Has(lnk, Exe, @"C:\Program Files\Acme\logo.ico");
        pc.Links[lnk] = (Exe, @"C:\Program Files\Acme\logo.ico,0");
        var icons = Find(pc, new("Acme", "", lnk));
        Assert.Equal([IconSource.Shortcut, IconSource.Program, IconSource.FileType], icons.Select(x => x.Source));
        Assert.Equal(Exe, icons[2].Path);
    }

    [Fact]
    public void AShortcutThatPointsAtAShortcutDoesNotLoopForever()
    {
        var a = @"C:\L\a.lnk"; var b = @"C:\L\b.lnk";
        var pc = new FakeIconPc().Has(a, b);
        pc.Links[a] = (b, ""); pc.Links[b] = (a, "");
        Assert.Empty(Find(pc, new("Loop", "", a)));
    }

    [Fact]
    public void ABareNameIsFoundOnThePathOrInAppPaths()
    {
        var pc = new FakeIconPc().Has(@"C:\Tools\tool.exe", @"C:\Program Files\Chrome\chrome.exe");
        pc.OnPath["tool.exe"] = @"C:\Tools\tool.exe";
        pc.Registered["chrome.exe"] = @"C:\Program Files\Chrome\chrome.exe";
        Assert.Equal(@"C:\Tools\tool.exe", Find(pc, new("Tool", "tool -x"))[0].Path);
        var chrome = Find(pc, new("Chrome", "chrome.exe --background"));
        Assert.Equal(new IconRef(@"C:\Program Files\Chrome\chrome.exe", 0, IconSource.AppPaths), chrome[0]);
    }

    [Fact]
    public void AMovedProgramIsFoundThroughAppPathsByItsFileName()
    {
        var pc = new FakeIconPc().Has(@"D:\Moved\acme.exe");
        pc.Registered["acme.exe"] = @"D:\Moved\acme.exe";
        Assert.Equal(new IconRef(@"D:\Moved\acme.exe", 0, IconSource.AppPaths), Find(pc, new("Acme", @"""C:\Old\acme.exe"""))[0]);
    }

    [Fact]
    public void ADoubledBackslashIsTidiedBecauseTheShellRejectsIt()
    {
        var pc = new FakeIconPc().Has(@"C:\Program Files\AMD\CNext\cncmd.exe", @"C:\Program Files\AMD\CNext\\cncmd.exe");
        Assert.Equal(@"C:\Program Files\AMD\CNext\cncmd.exe", Find(pc, new("StartCN", @"""C:\Program Files\AMD\CNext\\cncmd.exe"" go"))[0].Path);
    }

    // ---- Store apps ----

    private static string Manifest(string visual) => $"""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"><Applications><Application Id="App" Executable="app\Tool.exe"><uap:VisualElements {visual} /></Application></Applications></Package>""";

    [Fact]
    public void AStoreAppShowsTheLogoGlyphForTheCurrentThemeNearFortyEightBeforeItsExecutable()
    {
        var package = @"C:\Program Files\WindowsApps\Acme.Tool_1.0_x64__abc";
        var exe = $@"{package}\app\Tool.exe";
        var pc = new FakeIconPc().Has(exe,
            $@"{package}\Assets\Square44x44Logo.scale-100.png", $@"{package}\Assets\Square44x44Logo.scale-200.png",
            $@"{package}\Assets\Square44x44Logo.targetsize-48.png", $@"{package}\Assets\Square44x44Logo.targetsize-48_altform-unplated.png",
            $@"{package}\Assets\Square44x44Logo.targetsize-256_altform-unplated.png", $@"{package}\Assets\Square44x44Logo.targetsize-48_contrast-white.png",
            $@"{package}\Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png");
        pc.Texts[$@"{package}\AppxManifest.xml"] = Manifest("""Square44x44Logo="Assets\Square44x44Logo.png" Square150x150Logo="Assets\Wide.png" """);
        var dark = Find(pc, new("Tool", exe, exe), dark: true);
        Assert.Equal(new IconRef($@"{package}\Assets\Square44x44Logo.targetsize-48_altform-unplated.png", 0, IconSource.Package), dark[0]);
        Assert.Equal(new IconRef(exe, 0, IconSource.Program), dark[1]);
        Assert.Equal(new IconRef($@"{package}\Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png", 0, IconSource.Package), Find(pc, new("Tool", exe, exe), dark: false)[0]);
    }

    [Fact]
    public void AStoreAppWithOnlyTheOtherThemesGlyphUsesItsPlatedLogoInstead()
    {
        var package = @"C:\Program Files\WindowsApps\Acme.Tool_1.0_x64__abc";
        var exe = $@"{package}\Tool.exe";
        var pc = new FakeIconPc().Has(exe, $@"{package}\Assets\Square44x44Logo.targetsize-48_altform-unplated.png", $@"{package}\Assets\Square44x44Logo.scale-200.png");
        pc.Texts[$@"{package}\AppxManifest.xml"] = Manifest("""Square44x44Logo="Assets\Square44x44Logo.png" """);
        Assert.Equal($@"{package}\Assets\Square44x44Logo.scale-200.png", Find(pc, new("Tool", exe, exe), dark: false)[0].Path);
    }

    [Fact]
    public void AStoreAppWhoseLogoCannotBeFoundFallsBackToTheExecutable()
    {
        var package = @"C:\Program Files\WindowsApps\Acme.Tool_1.0_x64__abc";
        var exe = $@"{package}\Tool.exe";
        var pc = new FakeIconPc().Has(exe);
        pc.Texts[$@"{package}\AppxManifest.xml"] = "not xml at all <";
        Assert.Equal(IconSource.Program, Find(pc, new("Tool", exe, exe))[0].Source);
    }

    [Fact]
    public void APackageFolderAloneIsEnoughToFindAStoreAppsLogo()
    {
        // The Debloat page knows an app only by its folder, which may belong to an app that has no executable of its own to look at.
        var package = @"C:\Program Files\WindowsApps\Acme.Tool_1.0_x64__abc";
        var pc = new FakeIconPc().Has($@"{package}\Assets\Square44x44Logo.scale-200.png");
        pc.Texts[$@"{package}\AppxManifest.xml"] = Manifest("""Square44x44Logo="Assets\Square44x44Logo.png" """);

        Assert.Equal(new IconRef($@"{package}\Assets\Square44x44Logo.scale-200.png", 0, IconSource.Package), Find(pc, new("Acme.Tool", InstallLocation: package))[0]);
    }

    // ---- Update.exe launchers, installers and Start menu ----

    [Fact]
    public void SquirrelAppsShowTheAppInTheNewestVersionFolderNotUpdateExe()
    {
        var root = @"C:\Users\me\AppData\Local\Discord";
        var pc = new FakeIconPc().Has($@"{root}\Update.exe", $@"{root}\app-1.0.9\Discord.exe", $@"{root}\app-1.0.10\Discord.exe");
        var icons = Find(pc, new("Discord", $@"""{root}\Update.exe"" --processStart Discord.exe"));
        Assert.Equal($@"{root}\app-1.0.9\Discord.exe", icons[0].Path);
        Assert.Equal($@"{root}\Update.exe", icons[1].Path);
    }

    [Fact]
    public void AProgramListedUnderTheSameFolderSuppliesItsRegisteredIcon()
    {
        var pc = new FakeIconPc().Has(@"C:\Program Files\Acme Suite\bin\helper.exe", @"C:\Program Files\Acme Suite\suite.ico");
        pc.Installed.Add(new("Something Else Entirely", @"C:\Program Files\Acme Suite\suite.ico", @"C:\Program Files\Acme Suite"));
        var icons = Find(pc, new("Helper", @"""C:\Program Files\Acme Suite\bin\helper.exe"""));
        Assert.Contains(new IconRef(@"C:\Program Files\Acme Suite\suite.ico", 0, IconSource.DisplayIcon), icons);
    }

    [Fact]
    public void ProgramFilesItselfIsNeverTreatedAsAnAppFolder()
    {
        var pc = new FakeIconPc().Has(Exe, @"C:\Program Files\Other\other.ico");
        pc.Installed.Add(new("Unrelated", @"C:\Program Files\Other\other.ico", @"C:\Program Files"));
        Assert.DoesNotContain(Find(pc, new("Acme", $@"""{Exe}""")), x => x.Source == IconSource.DisplayIcon);
    }

    [Fact]
    public void ProgramsAreMatchedByNameWhenThePathsGiveNothingAwayAndVersionNoiseIsIgnored()
    {
        var pc = new FakeIconPc().Has(@"D:\Elsewhere\spot.exe", @"C:\Apps\Spot\spot.ico");
        pc.Installed.Add(new("Spot 2.3.1 (x64 edition)", @"C:\Apps\Spot\spot.ico", ""));
        pc.Installed.Add(new("Another", @"C:\Apps\Other\x.ico", ""));
        var icons = Find(pc, new("Spot", @"""D:\Elsewhere\spot.exe"""));
        Assert.Equal([IconSource.Program, IconSource.DisplayIcon, IconSource.FileType], icons.Select(x => x.Source));
    }

    [Fact]
    public void TheInstallFolderYieldsTheProgramNamedLikeTheAppNotTheInstallerOrHelper()
    {
        var folder = @"C:\Program Files\Foo Studio";
        var pc = new FakeIconPc().Has($@"{folder}\unins000.exe", $@"{folder}\crashhandler.exe", $@"{folder}\FooStudio.exe", $@"{folder}\updater.exe", $@"{folder}\foo_helper.exe");
        Assert.Equal($@"{folder}\FooStudio.exe", Find(pc, new("Foo Studio 4.2", "", "", "", folder))[0].Path);
    }

    [Fact]
    public void AFolderWithSeveralUnrelatedProgramsGivesNoGuess()
    {
        var folder = @"C:\Program Files\Suite";
        var pc = new FakeIconPc().Has($@"{folder}\one.exe", $@"{folder}\two.exe");
        Assert.Empty(Find(pc, new("Completely Different", "", "", "", folder)));
    }

    [Fact]
    public void AlonePresentProgramInTheFolderIsUsedEvenWhenTheNameDoesNotMatch()
    {
        var folder = @"C:\Program Files\Suite";
        var pc = new FakeIconPc().Has($@"{folder}\engine.exe", $@"{folder}\unins000.exe");
        Assert.Equal($@"{folder}\engine.exe", Find(pc, new("Product Name", "", "", "", folder))[0].Path);
    }

    [Fact]
    public void TheUninstallersFolderIsSearchedUnlessWindowsInstallerKeepsIt()
    {
        var folder = @"C:\Program Files\Foo";
        var pc = new FakeIconPc().Has($@"{folder}\uninstall.exe", $@"{folder}\foo.exe", @"C:\Windows\Installer\{ABC}\setup.exe");
        Assert.Equal($@"{folder}\foo.exe", Find(pc, new("Foo", "", "", "", "", $@"""{folder}\uninstall.exe"" /S"))[0].Path);
        Assert.Empty(Find(pc, new("Foo", "", "", "", "", @"""C:\Windows\Installer\{ABC}\setup.exe"" /x")));
    }

    [Fact]
    public void AnMsiProductsRecordedIconIsUsed()
    {
        var pc = new FakeIconPc().Has(@"C:\Windows\Installer\{AAA}\ProductIcon.exe");
        pc.ProductIcons["{AAA}"] = @"C:\Windows\Installer\{AAA}\ProductIcon.exe";
        Assert.Equal(IconSource.DisplayIcon, Find(pc, new("Msi App", ProductCode: "{AAA}"))[0].Source);
    }

    [Fact]
    public void AStartMenuShortcutNamedLikeTheAppSuppliesItsIconAsALastSpecificTry()
    {
        var lnk = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\7-Zip\7-Zip File Manager.lnk";
        var pc = new FakeIconPc().Has(lnk, @"C:\Program Files\7-Zip\7zFM.exe");
        pc.Links[lnk] = (@"C:\Program Files\7-Zip\7zFM.exe", "");
        pc.Shortcuts.Add(lnk);
        pc.Shortcuts.Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Other\Other.lnk");
        var icons = Find(pc, new("7-Zip 26.05 (x64 edition)"));
        Assert.Equal(new IconRef(@"C:\Program Files\7-Zip\7zFM.exe", 0, IconSource.Program), icons[0]);
    }

    [Fact]
    public void ScriptsAreOnlyEverShownWithTheirFileTypeIcon()
    {
        var pc = new FakeIconPc().Has(@"C:\Acme\start.bat");
        Assert.Equal([new IconRef(@"C:\Acme\start.bat", 0, IconSource.FileType)], Find(pc, new("Acme", @"cmd /c C:\Acme\start.bat")));
    }

    [Fact]
    public void TheSameIconIsNotOfferedTwice()
    {
        var pc = new FakeIconPc().Has(Exe);
        var icons = Find(pc, new("Acme", $@"""{Exe}""", Exe));
        Assert.Equal(icons.Count, icons.Select(x => x.Key).Distinct().Count());
    }
}
