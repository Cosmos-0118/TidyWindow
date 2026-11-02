using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>
/// The administrator helper keeps how each target looked in the protected store, and turning an item back on never trusts the copy that
/// arrives with the request: that one lives in a file any program running as the ordinary account can edit.
/// </summary>
public sealed class BackgroundVaultTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-quiet-vault-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private ProtectedStore Store() => new("quiet", root, enforce: false);
    private static BackgroundItem Item(params BackgroundTarget[] targets) => new() { Id = "item", Name = "The item", Group = BackgroundGroup.Optional, Summary = "s", Effect = "e", Targets = targets };
    private static BackgroundTarget Svc(string name, int start = 3, bool delayed = false) => new() { Kind = BackgroundTargetKind.Service, Name = name, DefaultStart = start, DefaultDelayed = delayed };
    private static BackgroundTarget Job(string path) => new() { Kind = BackgroundTargetKind.Task, Name = path };
    private static BackgroundTarget Peering() => new() { Kind = BackgroundTargetKind.Policy, Key = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", Name = "DODownloadMode", Block = 0, Values = [0, 1, 2, 3, 99, 100] };
    private const string PeeringKey = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization|DODownloadMode";
    private static BackgroundRequest Off => new("item", false);
    private static BackgroundRequest On(params BackgroundSaved[] saved) => new("item", true, saved);

    [Fact]
    public void TheHelperTurnsAnItemBackOnFromItsOwnCopyNotTheOneThatArrivedWithTheRequest()
    {
        var pc = new FakeBackgroundPc().Svc("Diag", start: 2, running: true, delayed: true);
        var store = Store();
        var changer = new LocalBackgroundChanger(pc, pc, [Item(Svc("Diag", 3))], store);

        var off = changer.Change(Off);
        Assert.True(off.Ok, off.Error);
        Assert.Equal(4, pc.ServiceList["Diag"].Start);
        Assert.True(store.Exists("item"));

        // The app's record has been edited to say the service was manual and not delayed. The helper puts back what it saw itself.
        var on = changer.Change(On(new(BackgroundTargetKind.Service, "Diag", true, 3)));
        Assert.True(on.Ok, on.Error);
        Assert.Equal(2, pc.ServiceList["Diag"].Start); Assert.True(pc.ServiceList["Diag"].Delayed); Assert.True(pc.ServiceList["Diag"].Running);
        Assert.False(store.Exists("item"));
    }

    [Fact]
    public void AForgedCopyCannotKeepAnItemOff()
    {
        var pc = new FakeBackgroundPc().Job(@"\Microsoft\a\b");
        var changer = new LocalBackgroundChanger(pc, pc, [Item(Job(@"\Microsoft\a\b"))], Store());
        Assert.True(changer.Change(Off).Ok);

        // "It was already off before TidyWindow touched it" would leave the task off for good.
        Assert.True(changer.Change(On(new(BackgroundTargetKind.Task, @"\Microsoft\a\b", false))).Ok);
        Assert.True(pc.TaskList[@"\Microsoft\a\b"].Enabled);
    }

    [Fact]
    public void WithoutACopyOfItsOwnTheHelperFallsBackToHowWindowsShipsThings_NotToTheRequest()
    {
        // Nothing here was turned off by the helper: a planted record names a disabled service and a peering mode of its choosing.
        var pc = new FakeBackgroundPc().Svc("RemoteRegistry", start: 4);
        pc.Policies[PeeringKey] = 0;
        var changer = new LocalBackgroundChanger(pc, pc, [Item(Svc("RemoteRegistry", 3), Peering())], Store());

        var outcome = changer.Change(On(new(BackgroundTargetKind.Service, "RemoteRegistry", true, 2), new(BackgroundTargetKind.Policy, "DODownloadMode", true, Value: 3)));

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(3, pc.ServiceList["RemoteRegistry"].Start);
        Assert.DoesNotContain("begin RemoteRegistry", pc.Writes);
        Assert.False(pc.Policies.ContainsKey(PeeringKey));
    }

    [Fact]
    public void AnEarlierOriginalIsKeptWhenTheRestOfAnItemIsTurnedOffAgainLater()
    {
        var pc = new FakeBackgroundPc().Svc("A", start: 2).Svc("B", start: 2);
        var changer = new LocalBackgroundChanger(pc, pc, [Item(Svc("A", 3), Svc("B", 3))], Store());
        Assert.True(changer.Change(Off).Ok);

        // A Windows update turns B back on, as manual this time; the person turns the item off again.
        pc.Svc("B", start: 3);
        Assert.True(changer.Change(Off).Ok);
        Assert.True(changer.Change(On()).Ok);

        Assert.Equal(2, pc.ServiceList["A"].Start);
        Assert.Equal(3, pc.ServiceList["B"].Start);
    }

    [Fact]
    public void AnItemIsNotTurnedOffWhenItsCopyCannotBeKept()
    {
        var pc = new FakeBackgroundPc().Svc("Diag", start: 2);
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "a file where the store's folder should be");
        var changer = new LocalBackgroundChanger(pc, pc, [Item(Svc("Diag", 3))], new ProtectedStore("quiet", blocked, enforce: false));

        var outcome = changer.Change(Off);

        Assert.False(outcome.Ok);
        Assert.Contains("could not be kept", outcome.Error);
        Assert.Empty(pc.Writes);
        Assert.Equal(2, pc.ServiceList["Diag"].Start);
    }

    [Fact]
    public void AChangeThatDidNotGoThroughLeavesNoCopyBehind()
    {
        var pc = new FakeBackgroundPc { FailOn = "task" }.Svc("S", start: 2).Job(@"\Microsoft\a\b");
        var store = Store();
        var changer = new LocalBackgroundChanger(pc, pc, [Item(Svc("S", 3), Job(@"\Microsoft\a\b"))], store);

        Assert.False(changer.Change(Off).Ok);

        Assert.False(store.Exists("item"));
        Assert.Equal(2, pc.ServiceList["S"].Start);
    }

    [Fact]
    public void AnItemThatIsAlreadyOnEverywhereNeedsNoCopy()
    {
        var pc = new FakeBackgroundPc().Svc("S", start: 2);
        var store = Store();
        store.Save("item", new List<BackgroundSaved> { new(BackgroundTargetKind.Service, "S", true, 3) });

        Assert.True(new LocalBackgroundChanger(pc, pc, [Item(Svc("S", 3))], store).Change(On()).Ok);

        Assert.Empty(pc.Writes);
        Assert.False(store.Exists("item"));
    }

    [Fact]
    public async Task OverThePipeTheHelperIgnoresTheSavedStateInTheRequest()
    {
        var pc = new FakeBackgroundPc().Svc("Diag", start: 2, running: true, delayed: true);
        pc.Policies[PeeringKey] = 1;
        var item = Item(Svc("Diag", 3), Peering());
        using var session = new BackgroundSession(pipe => BackgroundHost.RunAsync([BackgroundHost.Command, pipe], new LocalBackgroundChanger(pc, pc, [item], Store()), () => true, TimeSpan.FromSeconds(20)));
        await session.OpenAsync();

        Assert.True(session.Change(Off).Ok);
        Assert.Equal(4, pc.ServiceList["Diag"].Start); Assert.Equal(0, pc.Policies[PeeringKey]);

        var on = session.Change(On(new(BackgroundTargetKind.Service, "Diag", false), new(BackgroundTargetKind.Policy, "DODownloadMode", true, Value: 3)));

        Assert.True(on.Ok, on.Error);
        Assert.Equal(2, pc.ServiceList["Diag"].Start); Assert.True(pc.ServiceList["Diag"].Delayed);
        Assert.Equal(1, pc.Policies[PeeringKey]);
    }
}
