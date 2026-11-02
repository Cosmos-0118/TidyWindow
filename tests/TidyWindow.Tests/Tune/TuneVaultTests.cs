using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>
/// The administrator helper keeps the values a tweak replaced in the protected store, and putting the tweak back never trusts the copy that
/// arrives with the request: that one lives in a file any program running as the ordinary account can edit.
/// </summary>
public sealed class TuneVaultTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-tune-vault-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private const string Hvci = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";

    private ProtectedStore Store() => new("tune", root, enforce: false);
    private static TuneItem Item(params TuneTarget[] targets) => new() { Id = "item", Name = "The item", Group = TuneGroup.Speed, Scope = TuneScope.Machine, Summary = "s", Effect = "e", Targets = targets };
    private static TuneTarget MemoryIntegrity() => new() { Key = Hvci, Name = "Enabled", Value = "0", Normal = "1" };
    private static TuneTarget Boost() => new() { Key = Edge, Name = "StartupBoostEnabled", Value = "0", Normal = "1" };
    private static TuneTarget Background() => new() { Key = Edge, Name = "BackgroundModeEnabled", Value = "0", Normal = "1" };
    private static TuneRequest Apply => new("item", true);
    private static TuneRequest PutBack(params TuneSaved[] saved) => new("item", false, saved);
    private static LocalTuneChanger Helper(FakeTunePc pc, TuneItem item, ProtectedStore store) => new(pc, pc, [item], TuneScope.Machine, store);
    private static TuneValue? Read(FakeTunePc pc, string key, string name) => pc.Read(TuneScope.Machine, key, name);

    [Fact]
    public void TheHelperPutsBackItsOwnCopyNotTheOneThatArrivedWithTheRequest()
    {
        var pc = new FakeTunePc().Has(TuneScope.Machine, Hvci, "Enabled", "1");
        var store = Store();
        var changer = Helper(pc, Item(MemoryIntegrity()), store);

        var applied = changer.Change(Apply);
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal("0", Read(pc, Hvci, "Enabled")!.Text);
        Assert.True(store.Exists("item"));

        // The app's record has been edited to make memory integrity come back as text, which Windows would not read as "on".
        var back = changer.Change(PutBack(new(Hvci, "Enabled", true, TuneKind.String, "x", false)));
        Assert.True(back.Ok, back.Error);
        Assert.Equal(new TuneValue(TuneKind.Dword, "1"), Read(pc, Hvci, "Enabled"));
        Assert.False(store.Exists("item"));
    }

    [Fact]
    public void AForgedCopyCannotKeepATweakAppliedOrChooseAnotherNumber()
    {
        var pc = new FakeTunePc().Has(TuneScope.Machine, Hvci, "Enabled", "1");
        var changer = Helper(pc, Item(MemoryIntegrity()), Store());
        Assert.True(changer.Change(Apply).Ok);

        // "It was already applied before TidyWindow touched it" would leave memory integrity off for good.
        Assert.True(changer.Change(PutBack(new(Hvci, "Enabled", true, TuneKind.Dword, "0", true))).Ok);
        Assert.Equal("1", Read(pc, Hvci, "Enabled")!.Text);

        Assert.True(changer.Change(Apply).Ok);
        // A number of the right kind is ignored just the same.
        Assert.True(changer.Change(PutBack(new(Hvci, "Enabled", true, TuneKind.Dword, "4294967295", false))).Ok);
        Assert.Equal("1", Read(pc, Hvci, "Enabled")!.Text);
    }

    [Fact]
    public void WithoutACopyOfItsOwnTheHelperUsesTheCatalogsNormalValue_NotTheRequest()
    {
        // Applied by something else, or by an earlier version that kept no copy: the request's "original" is still not written.
        var pc = new FakeTunePc().Has(TuneScope.Machine, Hvci, "Enabled", "0");
        var changer = Helper(pc, Item(MemoryIntegrity()), Store());

        var back = changer.Change(PutBack(new(Hvci, "Enabled", true, TuneKind.Dword, "4294967295", false)));

        Assert.True(back.Ok, back.Error);
        Assert.Equal("1", Read(pc, Hvci, "Enabled")!.Text);
    }

    [Fact]
    public void AnEarlierOriginalIsKeptWhenTheRestOfATweakIsAppliedAgainLater()
    {
        var pc = new FakeTunePc().Has(TuneScope.Machine, Edge, "StartupBoostEnabled", "1").Has(TuneScope.Machine, Edge, "BackgroundModeEnabled", "1");
        var changer = Helper(pc, Item(Boost(), Background()), Store());
        Assert.True(changer.Change(Apply).Ok);

        // A policy refresh puts one value back, to something else; the person turns the tweak on again.
        pc.Has(TuneScope.Machine, Edge, "BackgroundModeEnabled", "7");
        Assert.True(changer.Change(Apply).Ok);
        Assert.True(changer.Change(PutBack()).Ok);

        Assert.Equal("1", Read(pc, Edge, "StartupBoostEnabled")!.Text);
        Assert.Equal("7", Read(pc, Edge, "BackgroundModeEnabled")!.Text);
    }

    [Fact]
    public void ATweakIsNotAppliedWhenItsCopyCannotBeKept()
    {
        var pc = new FakeTunePc().Has(TuneScope.Machine, Hvci, "Enabled", "1");
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "a file where the store's folder should be");
        var changer = Helper(pc, Item(MemoryIntegrity()), new ProtectedStore("tune", blocked, enforce: false));

        var outcome = changer.Change(Apply);

        Assert.False(outcome.Ok);
        Assert.Contains("could not be kept", outcome.Error);
        Assert.Empty(pc.Writes);
        Assert.Equal("1", Read(pc, Hvci, "Enabled")!.Text);
    }

    [Fact]
    public void AChangeThatDidNotGoThroughLeavesNoCopyBehind()
    {
        var pc = new FakeTunePc { FailOnWrite = 2 }.Has(TuneScope.Machine, Edge, "StartupBoostEnabled", "1").Has(TuneScope.Machine, Edge, "BackgroundModeEnabled", "1");
        var store = Store();

        Assert.False(Helper(pc, Item(Boost(), Background()), store).Change(Apply).Ok);

        Assert.False(store.Exists("item"));
        Assert.Equal("1", Read(pc, Edge, "StartupBoostEnabled")!.Text);
    }

    [Fact]
    public void ATweakThatIsNotAppliedAnywhereNeedsNoCopy()
    {
        var pc = new FakeTunePc().Has(TuneScope.Machine, Hvci, "Enabled", "1");
        var store = Store();
        store.Save("item", new List<TuneSaved> { new(Hvci, "Enabled", true, TuneKind.Dword, "1", false) });

        Assert.True(Helper(pc, Item(MemoryIntegrity()), store).Change(PutBack()).Ok);

        Assert.Empty(pc.Writes);
        Assert.False(store.Exists("item"));
    }

    [Fact]
    public async Task OverThePipeTheHelperIgnoresTheSavedValuesInTheRequest()
    {
        var pc = new FakeTunePc().Has(TuneScope.Machine, Hvci, "Enabled", "1");
        var item = Item(MemoryIntegrity());
        using var session = new TuneSession(pipe => TuneHost.RunAsync([TuneHost.Command, pipe], Helper(pc, item, Store()), () => true, TimeSpan.FromSeconds(20)));
        await session.OpenAsync();

        Assert.True(session.Change(Apply).Ok);
        Assert.Equal("0", Read(pc, Hvci, "Enabled")!.Text);

        var back = session.Change(PutBack(new(Hvci, "Enabled", true, TuneKind.String, "x", false)));

        Assert.True(back.Ok, back.Error);
        Assert.Equal(new TuneValue(TuneKind.Dword, "1"), Read(pc, Hvci, "Enabled"));
    }
}
