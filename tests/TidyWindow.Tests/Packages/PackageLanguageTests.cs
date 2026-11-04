using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>WinGet speaks the language of Windows. What TidyWindow concludes from it must not depend on the words, only on its codes and on how its output is laid out.</summary>
public sealed class PackageLanguageTests
{
    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => @"C:\managers\winget.exe";
        public string PowerShell => "powershell.exe";
    }

    private sealed class Runner(CommandResult result) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private static readonly ManagerStatus Winget = new(ManagerDefinition.All[0], ManagerHealth.Ready, "1.9", @"C:\managers\winget.exe", "");

    private static Task<InventoryResult> Installed(CommandResult result) => new PackageService(new Runner(result), new Locator()).ReadInstalledAsync(Winget);

    [Theory]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("No se encontró ningún paquete instalado que coincida con los criterios de entrada.")]
    [InlineData("Es wurde kein installiertes Paket gefunden, das den Eingabekriterien entspricht.")]
    [InlineData("入力条件に一致するインストール済みパッケージが見つかりません。")]
    [InlineData("\\\r  \r-\r")]
    public async Task AnEmptyListIsEmptyInEveryLanguage(string output)
    {
        var result = await Installed(new(0, output, ""));
        Assert.Null(result.Error);
        Assert.Empty(result.Apps);
    }

    [Fact]
    public async Task ATableThatCannotBeReadIsStillReportedAsOne()
    {
        var result = await Installed(new(0, "Alpha   Beta   Gamma\nfoo     bar    baz\n", ""));
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ALocalizedTableIsReadByItsLayoutAlone()
    {
        const string german = "Name           ID             Version\n------------------------------------------\nGit            Git.Git        2.45.1\n";
        var result = await Installed(new(0, german, ""));
        Assert.Null(result.Error);
        var app = Assert.Single(result.Apps);
        Assert.Equal("Git.Git", app.Id);
        Assert.Equal("2.45.1", app.Version);
    }

    [Fact]
    public void ACommandThatNeedsAdministratorRightsIsRecognizedByWingetsCodeWhateverTheLanguage()
    {
        Assert.True(InstallFailure.NeedsAdministrator(new(unchecked((int)0x8A150019), "Dieser Befehl erfordert Administratorrechte.", "")));
        Assert.False(InstallFailure.NeedsAdministrator(new(unchecked((int)0x8A150019) + 1, "Dieser Befehl erfordert Administratorrechte.", "")));
        Assert.False(InstallFailure.NeedsAdministrator(new(0, "", "")));
        Assert.True(InstallFailure.NeedsAdministrator(new(1, "Administrator privileges required", "")));
    }

    [Fact]
    public void OnlyTableShapedOutputLooksLikeATable()
    {
        Assert.False(PackageOutputParser.LooksLikeTable("No installed package found matching input criteria."));
        Assert.False(PackageOutputParser.LooksLikeTable(""));
        Assert.True(PackageOutputParser.LooksLikeTable("Name   Id   Version\n-----------------\n"));
        Assert.True(PackageOutputParser.LooksLikeTable("a   b   c"));
    }
}
