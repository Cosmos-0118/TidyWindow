using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

internal sealed class FakeTunePc : ITuneSystem, ITuneWriter
{
    public string Edition { get; set; } = "Professional";
    public int Build { get; set; } = 26300;
    public int Revision { get; set; } = 9999;
    public Dictionary<string, TuneValue> Values = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes = [];
    public bool DenyWrites, IgnoreWrites;
    /// <summary>The write number (1 is the first) that fails, to test undoing a half-finished change.</summary>
    public int FailOnWrite;

    private static string Id(TuneScope scope, string key, string name) => $"{scope}|{key}|{name}";

    public TuneValue? Read(TuneScope scope, string key, string name) => Values.GetValueOrDefault(Id(scope, key, name));

    public FakeTunePc Has(TuneScope scope, string key, string name, string text, TuneKind kind = TuneKind.Dword)
    {
        Values[Id(scope, key, name)] = new(kind, text);
        return this;
    }

    private void Guard(string what)
    {
        if (DenyWrites) throw new UnauthorizedAccessException("denied");
        Writes.Add(what);
        if (FailOnWrite == Writes.Count) throw new IOException("the write failed");
    }

    public void Set(TuneScope scope, string key, string name, TuneKind kind, string text)
    {
        Guard($"set {name} {text}");
        if (!IgnoreWrites) Values[Id(scope, key, name)] = new(kind, text);
    }

    public void Delete(TuneScope scope, string key, string name)
    {
        Guard($"delete {name}");
        if (!IgnoreWrites) Values.Remove(Id(scope, key, name));
    }

    public void DeleteKey(TuneScope scope, string key)
    {
        Guard($"remove {key}");
        foreach (var id in Values.Keys.Where(x => x.StartsWith($"{scope}|{key}\\", StringComparison.OrdinalIgnoreCase)).ToList()) Values.Remove(id);
    }
}

public sealed class TuneCatalogTests
{
    private static TuneItem Item(TuneScope scope = TuneScope.User, params TuneTarget[] targets) => new() { Id = "item", Name = "Item", Group = TuneGroup.Comfort, Scope = scope, Summary = "s", Effect = "e", Targets = targets };
    private static TuneTarget Target(string key = @"Control Panel\Mouse", string name = "MouseSpeed", string value = "0", TuneKind kind = TuneKind.String, string? normal = "1", TuneRule rule = TuneRule.Exactly) =>
        new() { Key = key, Name = name, Kind = kind, Value = value, Normal = normal, Rule = rule };

    [Fact]
    public void TheShippedCatalogIsValidAndReadyToUse()
    {
        var catalog = TuneCatalog.Load();
        Assert.Equal(16, catalog.Count);
        Assert.Empty(TuneGuard.Validate(catalog));
        foreach (var group in Enum.GetValues<TuneGroup>()) Assert.Contains(catalog, x => x.Group == group);
        Assert.All(catalog, x => { Assert.NotEmpty(x.Summary); Assert.NotEmpty(x.Effect); Assert.NotEmpty(x.Sources); });
    }

    [Fact]
    public void EveryShippedValueIsOneTheGuardAllowsInTheItemsOwnScope()
    {
        foreach (var item in TuneCatalog.Load())
            foreach (var target in item.Targets)
            {
                Assert.True(TuneGuard.AllowsValue(item.Scope, target.Key, target.Name), $"{item.Id}: {target.Key}");
                Assert.False(TuneGuard.AllowsValue(item.Scope == TuneScope.User ? TuneScope.Machine : TuneScope.User, target.Key, target.Name), $"{item.Id} is allowed in both registries");
            }
    }

    [Fact]
    public void TheSettingsThatOnlyHaveForumFolkloreBehindThemAreNotOffered()
    {
        var names = TuneCatalog.Load().SelectMany(x => x.Targets).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folklore in new[] { "SystemResponsiveness", "NetworkThrottlingIndex", "Win32PrioritySeparation", "DisablePagingExecutive", "LargeSystemCache", "IRPStackSize", "TcpAckFrequency", "TCPNoDelay", "DisabledComponents", "EnablePrefetcher" })
            Assert.DoesNotContain(folklore, names);
    }

    [Fact]
    public void NothingThatBelongsToTheQuietPageOrToSecurityIsInTheCatalog()
    {
        foreach (var target in TuneCatalog.Load().SelectMany(x => x.Targets))
        {
            Assert.DoesNotContain(@"\Services\", target.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Defender", target.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DataCollection", target.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GameDVR", target.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WindowsAI", target.Key, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\WinDefend", "Start")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run", "Anything")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell")]
    [InlineData(@"Control Panel\Mouse", "SomethingElse")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware")]
    public void ValuesOutsideTheAllowedListAreRefused(string key, string name) =>
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(key, name))]));

    [Fact]
    public void AValueAllowedInOneRegistryIsRefusedInTheOther()
    {
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.Machine, Target())]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", "1", TuneKind.Dword, "0"))]));
    }

    [Fact]
    public void BadCatalogEntriesAreExplained()
    {
        Assert.Empty(TuneGuard.Validate([Item(TuneScope.User, Target())]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(value: "x", kind: TuneKind.Dword))]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(value: "-1", kind: TuneKind.Dword))]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(value: "5000000000", kind: TuneKind.Dword))]));
        // The "normal" value must not already count as changed, or turning the item off would do nothing.
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(value: "0", normal: "0"))]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(value: "4", normal: null, rule: TuneRule.ClearBits))]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target(kind: TuneKind.Other))]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User)]));
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, Target()), Item(TuneScope.User, Target())]));
        Assert.NotEmpty(TuneGuard.Validate([new TuneItem { Id = "Bad Id", Name = "n", Group = TuneGroup.Speed, Scope = TuneScope.User, Summary = "s", Effect = "e", Targets = [Target()] }]));
    }

    [Fact]
    public void OnlyTheKeyThisPageCreatesMayBeRemoved()
    {
        Assert.True(TuneGuard.AllowsRemoval(TuneScope.User, TuneGuard.ClassicMenuClsid));
        Assert.False(TuneGuard.AllowsRemoval(TuneScope.User, @"Software\Classes\CLSID"));
        Assert.False(TuneGuard.AllowsRemoval(TuneScope.Machine, TuneGuard.ClassicMenuClsid));
        Assert.False(TuneGuard.AllowsRemoval(TuneScope.User, @"Software\Microsoft"));
        var inside = new TuneTarget { Key = @"Control Panel\Mouse", Name = "MouseSpeed", Kind = TuneKind.String, Value = "0", RemoveKey = TuneGuard.ClassicMenuClsid };
        Assert.NotEmpty(TuneGuard.Validate([Item(TuneScope.User, inside)]));
    }
}

public sealed class TuneRuleTests
{
    private static TuneTarget Target(TuneRule rule, string value, TuneKind kind = TuneKind.String, string? normal = null) => new() { Key = "k", Name = "n", Kind = kind, Rule = rule, Value = value, Normal = normal };
    private static TuneValue Text(string text) => new(TuneKind.String, text);

    [Fact]
    public void ExactlyComparesNumbersAsNumbersAndTextWithoutCase()
    {
        Assert.True(TuneRules.IsApplied(Target(TuneRule.Exactly, "0"), Text("0")));
        Assert.True(TuneRules.IsApplied(Target(TuneRule.Exactly, "0"), new(TuneKind.Dword, "0")));
        Assert.True(TuneRules.IsApplied(Target(TuneRule.Exactly, "NotSpecified"), Text("notspecified")));
        Assert.False(TuneRules.IsApplied(Target(TuneRule.Exactly, "0"), Text("1")));
        Assert.False(TuneRules.IsApplied(Target(TuneRule.Exactly, "0"), null));
        Assert.True(TuneRules.IsApplied(Target(TuneRule.Exactly, ""), Text("")));
    }

    [Fact]
    public void AtMostAcceptsAnythingAlreadyAsLowAndNeverAnAbsentOrUnreadableValue()
    {
        var rule = Target(TuneRule.AtMost, "5000");
        Assert.True(TuneRules.IsApplied(rule, Text("5000")));
        Assert.True(TuneRules.IsApplied(rule, Text("2000")));
        Assert.False(TuneRules.IsApplied(rule, Text("20000")));
        Assert.False(TuneRules.IsApplied(rule, Text("soon")));
        Assert.False(TuneRules.IsApplied(rule, null));
        Assert.False(TuneRules.IsApplied(rule, new(TuneKind.Other, "")));
    }

    [Fact]
    public void ClearBitsLooksOnlyAtItsOwnBitsAndKeepsTheRest()
    {
        var rule = Target(TuneRule.ClearBits, "4", normal: "510");
        Assert.False(TuneRules.IsApplied(rule, Text("510")));
        Assert.True(TuneRules.IsApplied(rule, Text("506")));
        Assert.True(TuneRules.IsApplied(rule, Text("26")));
        Assert.Equal("506", TuneRules.Wanted(rule, Text("510")));
        Assert.Equal("58", TuneRules.Wanted(rule, Text("62")));
        // Nothing there yet: start from what Windows normally has.
        Assert.Equal("506", TuneRules.Wanted(rule, null));
    }
}

public sealed class TuneScanTests
{
    private static TuneTarget Mouse(string value = "0", string? normal = "1") => new() { Key = @"Control Panel\Mouse", Name = "MouseSpeed", Kind = TuneKind.String, Value = value, Normal = normal };
    private static TuneTarget Threshold() => new() { Key = @"Control Panel\Mouse", Name = "MouseThreshold1", Kind = TuneKind.String, Value = "0", Normal = "6" };
    private static TuneItem Item(string id, params TuneTarget[] targets) => new() { Id = id, Name = id, Group = TuneGroup.Comfort, Scope = TuneScope.User, Summary = "s", Effect = "e", Targets = targets };
    private const string Key = @"Control Panel\Mouse";

    [Fact]
    public void AnItemIsAppliedPartlyAppliedOrNot()
    {
        var item = Item("mouse", Mouse(), Threshold());
        var pc = new FakeTunePc().Has(TuneScope.User, Key, "MouseSpeed", "1", TuneKind.String).Has(TuneScope.User, Key, "MouseThreshold1", "6", TuneKind.String);
        Assert.False(Assert.Single(TuneScan.Run(pc, [item]).Entries).Applied);

        pc.Has(TuneScope.User, Key, "MouseSpeed", "0", TuneKind.String);
        var partly = Assert.Single(TuneScan.Run(pc, [item]).Entries);
        Assert.True(partly.Partly); Assert.False(partly.Applied);

        pc.Has(TuneScope.User, Key, "MouseThreshold1", "0", TuneKind.String);
        var all = Assert.Single(TuneScan.Run(pc, [item]).Entries);
        Assert.True(all.Applied); Assert.False(all.Partly);
    }

    [Fact]
    public void AnItemForANewerWindowsIsLeftOutAndCounted()
    {
        var item = new TuneItem { Id = "new", Name = "n", Group = TuneGroup.Comfort, Scope = TuneScope.User, Summary = "s", Effect = "e", MinBuild = 22621, Targets = [Mouse()] };
        var result = TuneScan.Run(new FakeTunePc { Build = 19045 }, [item]);
        Assert.Empty(result.Entries); Assert.Equal(1, result.NotOnThisPc);
        Assert.Single(TuneScan.Run(new FakeTunePc { Build = 22621 }, [item]).Entries);
    }

    [Fact]
    public void AnItemThatCameInALaterUpdateOfTheSameBuildIsLeftOutUntilThen()
    {
        var item = new TuneItem { Id = "late", Name = "n", Group = TuneGroup.Cleaner, Scope = TuneScope.User, Summary = "s", Effect = "e", MinBuild = 26300, MinRevision = 8697, Targets = [Mouse()] };
        Assert.Empty(TuneScan.Run(new FakeTunePc { Build = 26300, Revision = 8696 }, [item]).Entries);
        Assert.Single(TuneScan.Run(new FakeTunePc { Build = 26300, Revision = 8697 }, [item]).Entries);
        Assert.Single(TuneScan.Run(new FakeTunePc { Build = 26300, Revision = 9818 }, [item]).Entries);
        // A newer build has it whatever its update number is.
        Assert.Single(TuneScan.Run(new FakeTunePc { Build = 26400, Revision = 1 }, [item]).Entries);
        Assert.Empty(TuneScan.Run(new FakeTunePc { Build = 26200, Revision = 9999 }, [item]).Entries);
    }

    [Fact]
    public void TheWebSearchSwitchIsTheOneWindowsSettingsShowsAndNeedsTheBuildThatHasIt()
    {
        var item = TuneCatalog.Load().Single(x => x.Id == "web-search");
        var target = Assert.Single(item.Targets);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", target.Key);
        Assert.Equal("IsWebSuggestionsEnabled", target.Name);
        Assert.Equal("0", target.Value);
        Assert.Equal(TuneScope.User, item.Scope);

        var pc = new FakeTunePc { Build = 26300, Revision = 9818 };
        Assert.False(Assert.Single(TuneScan.Run(pc, [item]).Entries).Applied);
        pc.Has(TuneScope.User, target.Key, target.Name, "0");
        Assert.True(Assert.Single(TuneScan.Run(pc, [item]).Entries).Applied);
        Assert.Empty(TuneScan.Run(new FakeTunePc { Build = 26100, Revision = 9999 }, [item]).Entries);
    }

    [Fact]
    public void WindowsHomeIsToldItsSettingIsIgnored()
    {
        var item = new TuneItem { Id = "pro", Name = "n", Group = TuneGroup.Cleaner, Scope = TuneScope.User, NeedsPro = true, Summary = "s", Effect = "e", Targets = [Mouse()] };
        var home = Assert.Single(TuneScan.Run(new FakeTunePc { Edition = "Core" }, [item]).Entries);
        Assert.Contains("Home", home.Lock); Assert.False(home.CanChange);
        Assert.True(Assert.Single(TuneScan.Run(new FakeTunePc { Edition = "Professional" }, [item]).Entries).CanChange);
    }

    [Fact]
    public void ValueOfAnUnexpectedTypeIsLeftAlone()
    {
        var pc = new FakeTunePc().Has(TuneScope.User, Key, "MouseSpeed", "", TuneKind.Other);
        var entry = Assert.Single(TuneScan.Run(pc, [Item("mouse", Mouse())]).Entries);
        Assert.NotNull(entry.Lock); Assert.False(entry.CanChange);
    }
}

public sealed class TuneChangeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-tune-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private const string Mouse = @"Control Panel\Mouse";
    private const string Flags = @"Control Panel\Accessibility\StickyKeys";
    private const string Clsid = TuneGuard.ClassicMenuClsid;

    private static TuneItem Item(TuneScope scope = TuneScope.User, params TuneTarget[] targets) => new() { Id = "item", Name = "The item", Group = TuneGroup.Comfort, Scope = scope, Takes = TuneEffect.SignOut, Summary = "s", Effect = "e", Targets = targets };
    private static TuneTarget Speed() => new() { Key = Mouse, Name = "MouseSpeed", Kind = TuneKind.String, Value = "0", Normal = "1" };
    private static TuneTarget Threshold() => new() { Key = Mouse, Name = "MouseThreshold1", Kind = TuneKind.String, Value = "0", Normal = "6" };
    private static TuneTarget Sticky() => new() { Key = Flags, Name = "Flags", Kind = TuneKind.String, Rule = TuneRule.ClearBits, Value = "4", Normal = "510" };
    private static TuneTarget Classic() => new() { Key = Clsid + @"\InprocServer32", Name = "", Kind = TuneKind.String, Value = "", RemoveKey = Clsid };
    private static LocalTuneChanger Changer(FakeTunePc pc, TuneItem item, TuneScope? only = null) => new(pc, pc, [item], only);
    private static FakeTunePc Pc() => new FakeTunePc().Has(TuneScope.User, Mouse, "MouseSpeed", "1", TuneKind.String).Has(TuneScope.User, Mouse, "MouseThreshold1", "6", TuneKind.String);
    private static string Text(FakeTunePc pc, string key, string name, TuneScope scope = TuneScope.User) => pc.Read(scope, key, name)?.Text ?? "<not set>";

    [Fact]
    public void ApplyingWritesTheValuesAndSavesHowTheyWere()
    {
        var pc = Pc(); var item = Item(TuneScope.User, Speed(), Threshold());
        var outcome = Changer(pc, item).Change(new("item", true));

        Assert.True(outcome.Ok);
        Assert.Equal("0", Text(pc, Mouse, "MouseSpeed")); Assert.Equal("0", Text(pc, Mouse, "MouseThreshold1"));
        Assert.Equal("1", outcome.Before!.Single(x => x.Name == "MouseSpeed").Text);
        Assert.All(outcome.Before!, x => { Assert.True(x.WasSet); Assert.False(x.WasApplied); });
        Assert.Contains("sign in", outcome.Note);
    }

    [Fact]
    public void OnlyWhatIsNotAlreadyAppliedIsWritten()
    {
        var pc = Pc().Has(TuneScope.User, Mouse, "MouseSpeed", "0", TuneKind.String);
        var outcome = Changer(pc, Item(TuneScope.User, Speed(), Threshold())).Change(new("item", true));

        Assert.True(outcome.Ok);
        Assert.Equal(["set MouseThreshold1 0"], pc.Writes);
        Assert.True(outcome.Before!.Single(x => x.Name == "MouseSpeed").WasApplied);
    }

    [Fact]
    public void ApplyingTwiceDoesTheSameAsOnce()
    {
        var pc = Pc(); var changer = Changer(pc, Item(TuneScope.User, Speed()));
        changer.Change(new("item", true));
        pc.Writes.Clear();

        var again = changer.Change(new("item", true));
        Assert.True(again.Ok); Assert.Empty(pc.Writes); Assert.Null(again.Before);
    }

    [Fact]
    public void PuttingBackRestoresExactlyWhatWasSaved()
    {
        var pc = Pc().Has(TuneScope.User, Mouse, "MouseSpeed", "2", TuneKind.String);
        var changer = Changer(pc, Item(TuneScope.User, Speed(), Threshold()));
        var before = changer.Change(new("item", true)).Before!;

        var outcome = changer.Change(new("item", false, before));

        Assert.True(outcome.Ok);
        Assert.Equal("2", Text(pc, Mouse, "MouseSpeed")); Assert.Equal("6", Text(pc, Mouse, "MouseThreshold1"));
    }

    [Fact]
    public void AValueThatWasNotThereIsRemovedAgainNotGuessed()
    {
        var pc = new FakeTunePc();
        var item = Item(TuneScope.User, new TuneTarget { Key = Mouse, Name = "MouseSpeed", Kind = TuneKind.String, Value = "0", Normal = "1" });
        var changer = Changer(pc, item);
        var before = changer.Change(new("item", true)).Before!;
        Assert.False(Assert.Single(before).WasSet);

        Assert.True(changer.Change(new("item", false, before)).Ok);
        Assert.Equal("<not set>", Text(pc, Mouse, "MouseSpeed"));
    }

    [Fact]
    public void WithoutASavedRecordTheNormalValueIsUsedOrTheValueRemoved()
    {
        var pc = Pc().Has(TuneScope.User, Mouse, "MouseSpeed", "0", TuneKind.String).Has(TuneScope.User, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", "1");
        var item = Item(TuneScope.User, Speed(), new TuneTarget { Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", Name = "TaskbarEndTask", Value = "1" });

        Assert.True(Changer(pc, item).Change(new("item", false)).Ok);
        Assert.Equal("1", Text(pc, Mouse, "MouseSpeed"));
        Assert.Equal("<not set>", Text(pc, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask"));
    }

    [Fact]
    public void ASettingThatAlreadyHadTheTweakedValueBeforeIsLeftAsItWasWhenPuttingBack()
    {
        var pc = Pc().Has(TuneScope.User, Mouse, "MouseSpeed", "0", TuneKind.String);
        var changer = Changer(pc, Item(TuneScope.User, Speed(), Threshold()));
        var before = changer.Change(new("item", true)).Before!;

        Assert.True(changer.Change(new("item", false, before)).Ok);
        Assert.Equal("0", Text(pc, Mouse, "MouseSpeed"));
        Assert.Equal("6", Text(pc, Mouse, "MouseThreshold1"));
    }

    [Fact]
    public void ClearingABitKeepsEveryOtherBitAndPuttingBackRestoresTheWholeNumber()
    {
        var pc = new FakeTunePc().Has(TuneScope.User, Flags, "Flags", "62", TuneKind.String);
        var changer = Changer(pc, Item(TuneScope.User, Sticky()));
        var before = changer.Change(new("item", true)).Before!;
        Assert.Equal("58", Text(pc, Flags, "Flags"));

        Assert.True(changer.Change(new("item", false, before)).Ok);
        Assert.Equal("62", Text(pc, Flags, "Flags"));
    }

    [Fact]
    public void TheClassicMenuKeyIsCreatedAndThenRemovedEntirely()
    {
        var pc = new FakeTunePc();
        var changer = Changer(pc, Item(TuneScope.User, Classic()));
        var before = changer.Change(new("item", true)).Before!;
        Assert.Equal("", pc.Read(TuneScope.User, Clsid + @"\InprocServer32", "")!.Text);

        Assert.True(changer.Change(new("item", false, before)).Ok);
        Assert.Null(pc.Read(TuneScope.User, Clsid + @"\InprocServer32", ""));
        Assert.Contains($"remove {Clsid}", pc.Writes);
    }

    [Fact]
    public void AFailureHalfWayThroughPutsEverythingBack()
    {
        var pc = Pc();
        pc.FailOnWrite = 2;
        var outcome = Changer(pc, Item(TuneScope.User, Speed(), Threshold())).Change(new("item", true));

        Assert.False(outcome.Ok); Assert.Equal("the write failed", outcome.Error);
        Assert.Equal("1", Text(pc, Mouse, "MouseSpeed")); Assert.Equal("6", Text(pc, Mouse, "MouseThreshold1"));
    }

    [Fact]
    public void AChangeWindowsDoesNotAcceptIsUndoneAndReported()
    {
        var pc = Pc();
        pc.IgnoreWrites = true;
        var outcome = Changer(pc, Item(TuneScope.User, Speed())).Change(new("item", true));
        Assert.False(outcome.Ok); Assert.Equal("Windows did not accept the change.", outcome.Error);
    }

    [Fact]
    public void WithoutPermissionNothingIsWrittenAndTheDenialIsPassedOn()
    {
        var pc = Pc();
        pc.DenyWrites = true;
        Assert.Throws<UnauthorizedAccessException>(() => Changer(pc, Item(TuneScope.User, Speed())).Change(new("item", true)));
        Assert.Empty(pc.Writes); Assert.Equal("1", Text(pc, Mouse, "MouseSpeed"));
    }

    [Fact]
    public void AValueOfAnUnexpectedTypeIsNeverOverwritten()
    {
        var pc = new FakeTunePc().Has(TuneScope.User, Mouse, "MouseSpeed", "", TuneKind.Other);
        var outcome = Changer(pc, Item(TuneScope.User, Speed())).Change(new("item", true));
        Assert.False(outcome.Ok); Assert.Empty(pc.Writes);
    }

    [Fact]
    public void TheAdministratorHelperRefusesAnythingButMachineSettings()
    {
        var pc = Pc();
        var outcome = Changer(pc, Item(TuneScope.User, Speed()), only: TuneScope.Machine).Change(new("item", true));
        Assert.False(outcome.Ok); Assert.Empty(pc.Writes);
    }

    [Fact]
    public void AnUnknownItemIsRefused() =>
        Assert.False(Changer(Pc(), Item(TuneScope.User, Speed())).Change(new("not-in-the-catalog", true)).Ok);

    // ---- Manager ----

    private sealed class Counter : ITuneChanger
    {
        public int Calls; public Func<TuneRequest, TuneOutcome> Run = _ => new(true);
        public TuneOutcome Change(TuneRequest request) { Calls++; return Run(request); }
    }

    private static TuneEntry Entry(FakeTunePc pc, TuneItem item) => Assert.Single(TuneScan.Run(pc, [item]).Entries);

    [Fact]
    public async Task AUserSettingNeverAsksForPermissionAndIsRemembered()
    {
        var pc = Pc(); var item = Item(TuneScope.User, Speed());
        var unlocks = 0; var helper = new Counter();
        var manager = new TuneManager(Changer(pc, item), helper, () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.True((await manager.SetAsync(Entry(pc, item), true)).Ok);

        Assert.Equal(0, unlocks); Assert.Equal(0, helper.Calls);
        Assert.Equal("The item", Assert.Single(manager.History.All()).Name);
    }

    [Fact]
    public async Task AUserSettingThatIsDeniedIsNeverHandedToTheAdministratorHelper()
    {
        var pc = Pc(); var item = Item(TuneScope.User, Speed());
        pc.DenyWrites = true;
        var unlocks = 0; var helper = new Counter();
        var manager = new TuneManager(Changer(pc, item), helper, () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.False((await manager.SetAsync(Entry(pc, item), true)).Ok);

        Assert.Equal(0, unlocks); Assert.Equal(0, helper.Calls); Assert.Empty(manager.History.All());
    }

    private static readonly TuneTarget Long = new() { Key = @"SYSTEM\CurrentControlSet\Control\FileSystem", Name = "LongPathsEnabled", Value = "1", Normal = "0" };

    [Fact]
    public async Task AMachineSettingThatNeedsRightsGetsOnePromptAndTheHelperDoesTheWork()
    {
        var item = Item(TuneScope.Machine, Long);
        var pc = new FakeTunePc().Has(TuneScope.Machine, Long.Key, "LongPathsEnabled", "0");
        pc.DenyWrites = true;
        var unlocks = 0;
        var helper = new Counter { Run = r => { pc.DenyWrites = false; try { return Changer(pc, item).Change(r); } finally { pc.DenyWrites = true; } } };
        var manager = new TuneManager(Changer(pc, item), helper, () => { unlocks++; return Task.CompletedTask; }, new(folder));

        Assert.True((await manager.SetAsync(Entry(pc, item), true)).Ok);

        Assert.Equal(1, unlocks); Assert.Equal(1, helper.Calls);
        Assert.Single(manager.History.All());
    }

    [Fact]
    public async Task ADeclinedPromptChangesNothingAndRemembersNothing()
    {
        var item = Item(TuneScope.Machine, Long);
        var pc = new FakeTunePc().Has(TuneScope.Machine, Long.Key, "LongPathsEnabled", "0");
        pc.DenyWrites = true;
        var manager = new TuneManager(Changer(pc, item), new Counter(), () => throw new OperationCanceledException(), new(folder));

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.SetAsync(Entry(pc, item), true));
        Assert.Empty(pc.Writes); Assert.Empty(manager.History.All());
    }

    [Fact]
    public async Task ALockedEntryNeverReachesAnyChanger()
    {
        var item = new TuneItem { Id = "item", Name = "n", Group = TuneGroup.Cleaner, Scope = TuneScope.User, NeedsPro = true, Summary = "s", Effect = "e", Targets = [Speed()] };
        var pc = new FakeTunePc { Edition = "Core" };
        var user = new Counter(); var machine = new Counter();
        var manager = new TuneManager(user, machine, () => Task.CompletedTask, new(folder));

        Assert.False((await manager.SetAsync(Entry(pc, item), true)).Ok);
        Assert.Equal(0, user.Calls + machine.Calls);
    }

    [Fact]
    public async Task PuttingBackUsesTheRememberedValuesAndForgetsThem()
    {
        var pc = Pc().Has(TuneScope.User, Mouse, "MouseSpeed", "2", TuneKind.String); var item = Item(TuneScope.User, Speed());
        var manager = new TuneManager(Changer(pc, item), new Counter(), () => Task.CompletedTask, new(folder));
        await manager.SetAsync(Entry(pc, item), true);
        Assert.Equal("0", Text(pc, Mouse, "MouseSpeed"));

        Assert.True((await manager.SetAsync(Entry(pc, item), false)).Ok);

        Assert.Equal("2", Text(pc, Mouse, "MouseSpeed"));
        Assert.Empty(manager.History.All());
    }

    [Fact]
    public async Task RestoreAllBringsEverythingBackAndForgetsIt()
    {
        var pc = Pc(); var item = Item(TuneScope.User, Speed(), Threshold());
        var manager = new TuneManager(Changer(pc, item), new Counter(), () => Task.CompletedTask, new(folder));
        await manager.SetAsync(Entry(pc, item), true);

        var results = await manager.RestoreAllAsync(pc, [item]);

        Assert.True(Assert.Single(results).Outcome.Ok);
        Assert.Empty(manager.History.All());
        Assert.Equal("1", Text(pc, Mouse, "MouseSpeed")); Assert.Equal("6", Text(pc, Mouse, "MouseThreshold1"));
    }

    [Fact]
    public async Task ItemsThatAreGoneOrWerePutBackElsewhereAreDroppedFromTheHistory()
    {
        var pc = Pc();
        var history = new TuneHistory(folder);
        history.Add(new("gone", "Gone", DateTimeOffset.Now, [new(Mouse, "Nope", true, TuneKind.String, "1", false)]));
        history.Add(new("item", "Back", DateTimeOffset.Now, [new(Mouse, "MouseSpeed", true, TuneKind.String, "1", false)]));
        var manager = new TuneManager(new Counter(), new Counter(), () => Task.CompletedTask, history);

        Assert.Empty(await manager.RestoreAllAsync(pc, [Item(TuneScope.User, Speed())]));
        Assert.Empty(history.All());
    }

    [Fact]
    public void TheHistorySurvivesRestartsAndABrokenFile()
    {
        new TuneHistory(folder).Add(new("a", "A", DateTimeOffset.Now, [new(Mouse, "MouseSpeed", false, TuneKind.String, "", false)]));
        var back = Assert.Single(new TuneHistory(folder).All());
        Assert.Equal("A", back.Name); Assert.False(Assert.Single(back.Saved).WasSet);
        File.WriteAllText(Path.Combine(folder, "changed.json"), "{ not json");
        Assert.Empty(new TuneHistory(folder).All());
    }

    [Fact]
    public void ApplyingTheRestOfAnItemKeepsTheOriginalOfAPartAlreadyChanged()
    {
        var history = new TuneHistory(folder);
        history.Add(new("item", "I", DateTimeOffset.Now, [new(Mouse, "MouseSpeed", true, TuneKind.String, "1", false)]));
        history.Add(new("item", "I", DateTimeOffset.Now, [new(Mouse, "MouseSpeed", true, TuneKind.String, "0", true), new(Mouse, "MouseThreshold1", true, TuneKind.String, "6", false)]));

        var saved = history.Get("item")!.Saved;
        Assert.Equal("1", saved.Single(x => x.Name == "MouseSpeed").Text);
        Assert.Equal("6", saved.Single(x => x.Name == "MouseThreshold1").Text);
    }
}

public sealed class TuneHostTests
{
    private static TuneSession Session(List<TuneRequest> seen, bool admin = true, Func<TuneRequest, TuneOutcome>? answer = null) =>
        new(pipe => TuneHost.RunAsync([TuneHost.Command, pipe], new Recorder(seen, answer ?? (_ => new(true))), () => admin, TimeSpan.FromSeconds(20)));

    private sealed class Recorder(List<TuneRequest> seen, Func<TuneRequest, TuneOutcome> answer) : ITuneChanger
    {
        public TuneOutcome Change(TuneRequest request) { seen.Add(request); return answer(request); }
    }

    [Fact]
    public async Task TheHelperPassesAChangeToTheChangerAndReturnsWhatWasSaved()
    {
        var seen = new List<TuneRequest>();
        var saved = new TuneSaved(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", true, TuneKind.Dword, "0", false);
        using var session = Session(seen, answer: _ => new(true, null, "note", [saved]));
        await session.OpenAsync();

        var outcome = session.Change(new("long-paths", true));

        Assert.True(outcome.Ok); Assert.Equal("note", outcome.Note);
        Assert.Equal(saved, Assert.Single(outcome.Before!));
        Assert.Equal("long-paths", Assert.Single(seen).Id);
    }

    [Fact]
    public async Task SavedValuesTravelToTheHelperWhenPuttingBack()
    {
        var seen = new List<TuneRequest>();
        using var session = Session(seen);
        await session.OpenAsync();

        session.Change(new("x", false, [new("k", "n", false, TuneKind.String, "", false)]));

        Assert.False(Assert.Single(Assert.Single(seen).Saved!).WasSet);
    }

    [Fact]
    public async Task AnErrorFromTheHelperComesBackAsAFailedOutcome()
    {
        using var session = Session([], answer: _ => new(false, "Windows did not accept the change."));
        await session.OpenAsync();
        var outcome = session.Change(new("x", true));
        Assert.False(outcome.Ok); Assert.Equal("Windows did not accept the change.", outcome.Error);
    }

    [Fact]
    public async Task AHelperWithoutAdministratorRightsIsNotAccepted()
    {
        using var session = Session([], admin: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ADeclinedPromptStopsEverything()
    {
        using var session = new TuneSession(_ => throw new OperationCanceledException("declined"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ClosingTheSessionStopsTheHelper()
    {
        var finished = new TaskCompletionSource();
        using var session = new TuneSession(async pipe =>
        {
            await TuneHost.RunAsync([TuneHost.Command, pipe], new Recorder([], _ => new(true)), () => true, TimeSpan.FromSeconds(20));
            finished.SetResult();
        });
        await session.OpenAsync();

        session.Close();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsOpen);
    }
}

public sealed class TuneLiveTests
{
    // Read-only: scans the real PC with the shipped catalog. Opt in like the other live checks.
    [Fact]
    public void TheShippedCatalogCanBeReadOnThisPc()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var result = TuneScan.Run(new WindowsTuneSystem(), TuneCatalog.Load());
        Assert.NotEmpty(result.Entries);
        Assert.Equal(TuneCatalog.Load().Count, result.Entries.Count + result.NotOnThisPc);
        foreach (var entry in result.Entries) Console.WriteLine($"{entry.Id,-22} applied={entry.Applied,-5} partly={entry.Partly,-5} lock={entry.Lock}");
    }
}
