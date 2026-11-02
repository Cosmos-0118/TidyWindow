using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The administrator helper's copies of removed items live in the protected store, and a put-back never trusts the app's copy.</summary>
public sealed class StartupVaultTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-vault-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static StartupRequest Remove(string id) => new(id, false, Action: StartupAction.Remove);
    private static StartupRequest Restore(string id, StartupVault? vault) => new(id, true, Action: StartupAction.Restore, Vault: vault);

    private static FakeStartupPc Pc()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Apps\setup.exe");
        pc.Runs.Add(new(StartupScope.User, false, "Setup", @"C:\Apps\setup.exe /finish", "RunOnce", Expandable: true));
        return pc;
    }

    [Fact]
    public void TheHelperPutsBackItsOwnCopyNotTheOneThatArrivedWithTheRequest()
    {
        var pc = Pc();
        var store = new ProtectedStore("startup", root, enforce: false);
        var changer = new LocalStartupChanger(pc, pc, store);
        var id = StartupScan.Run(pc).Entries[0].Id;

        var removed = changer.Change(Remove(id));
        Assert.True(removed.Ok, removed.Error);
        Assert.Empty(pc.Runs);
        Assert.True(store.Exists(id));

        // A record the ordinary account could have edited names another program. The helper ignores it.
        var forged = removed.Vault! with { Command = @"C:\Users\me\evil.exe" };
        var restored = changer.Change(Restore(id, forged));
        Assert.True(restored.Ok, restored.Error);
        Assert.Equal(@"C:\Apps\setup.exe /finish", Assert.Single(pc.Runs).Command);
        Assert.False(store.Exists(id));
    }

    [Fact]
    public void WithoutAProtectedCopyNothingIsPutBack()
    {
        var pc = Pc();
        var changer = new LocalStartupChanger(pc, pc, new ProtectedStore("startup", root, enforce: false));
        var id = StartupScan.Run(pc).Entries[0].Id;
        pc.Runs.Clear();

        var outcome = changer.Change(Restore(id, new StartupVault(StartupKind.RunKey, StartupScope.User, "RunOnce", "Setup", @"C:\Users\me\evil.exe")));

        Assert.False(outcome.Ok);
        Assert.Contains("protected copy", outcome.Error);
        Assert.Empty(pc.Runs);
        Assert.DoesNotContain(pc.Writes, x => x.StartsWith("restore", StringComparison.Ordinal));
    }

    [Fact]
    public void AnItemIsNotRemovedWhenItsCopyCannotBeKept()
    {
        var pc = Pc();
        Directory.CreateDirectory(root);
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "a file where the store's folder should be");
        var changer = new LocalStartupChanger(pc, pc, new ProtectedStore("startup", blocked, enforce: false));
        var id = StartupScan.Run(pc).Entries[0].Id;

        var outcome = changer.Change(Remove(id));

        Assert.False(outcome.Ok);
        Assert.Contains("could not be kept", outcome.Error);
        Assert.Single(pc.Runs);
        Assert.DoesNotContain(pc.Writes, x => x.StartsWith("remove", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAStoreTheRequestsCopyIsStillUsedForTheUsersOwnItems()
    {
        var pc = Pc();
        var changer = new LocalStartupChanger(pc, pc);
        var id = StartupScan.Run(pc).Entries[0].Id;
        var removed = changer.Change(Remove(id));
        Assert.True(changer.Change(Restore(id, removed.Vault)).Ok);
        Assert.Single(pc.Runs);
    }
}
