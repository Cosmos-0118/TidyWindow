using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class SmartSearchTests
{
    private sealed record Thing(string Name, string About);

    private static readonly Thing[] Things =
    [
        new("Restart sound", "No sound, crackling, or a headset that disappeared."),
        new("Restart Bluetooth", "Headphones or a mouse show as connected but do nothing."),
        new("Fix printing", "A printer says Printing forever."),
        new("Flush the DNS cache", "A website will not load while other sites work."),
        new("Reset the network", "The internet or Wi-Fi connection will not work.")
    ];

    private static List<string> Find(string query) => SmartSearch.Rank(Things, query, x => [new(x.Name, 3), new(x.About, 1)]).Select(x => x.Name).ToList();

    private static readonly string[] Apps = ["Microsoft Edge", "Visual Studio Code", "Google Chrome", "Mozilla Firefox", "7-Zip", "Notepad++"];

    private static List<string> FindApp(string query, bool related = false) => SmartSearch.Rank(Apps, query, x => [new(x, 4)], related).ToList();

    [Fact]
    public void ListsOfNamesCanTurnRelatedWordsOff()
    {
        // "store" is related to "microsoft" in the catalogs of fixes, but on a list of app names it must not find every Microsoft app.
        Assert.Contains("Microsoft Edge", SmartSearch.Rank(Apps, "store", x => [new(x, 4)], related: true));
        Assert.Empty(FindApp("store"));
    }

    [Theory]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("chrom", "Google Chrome")]
    [InlineData("firefx", "Mozilla Firefox")]
    [InlineData("crhome", "Google Chrome")]
    [InlineData("notepad", "Notepad++")]
    [InlineData("zip", "7-Zip")]
    [InlineData("code visual", "Visual Studio Code")]
    public void AppNamesAreFoundByInitialsPartsAndTypos(string query, string expected) => Assert.Contains(expected, FindApp(query));

    [Fact]
    public void ARowThatKeepsItsPreparedWordsIsRankedTheSameWay()
    {
        var prepared = Apps.ToDictionary(x => x, x => new SmartSearch.Prepared([new(x, 4)]));
        Assert.Equal(FindApp("co"), SmartSearch.RankPrepared(Apps, "co", x => prepared[x], related: false));
        Assert.Equal(Apps, SmartSearch.RankPrepared(Apps, "  ", x => prepared[x]));
    }

    [Fact]
    public void AnEmptyQueryKeepsEverythingInItsOwnOrder()
    {
        Assert.Equal(Things.Select(x => x.Name), Find(""));
        Assert.Equal(Things.Select(x => x.Name), Find("   "));
        Assert.True(SmartSearch.IsEmpty(" ,. "));
    }

    [Theory]
    [InlineData("blutooth", "Restart Bluetooth")]
    [InlineData("bluetoth", "Restart Bluetooth")]
    [InlineData("printr", "Fix printing")]
    [InlineData("prnting", "Fix printing")]
    [InlineData("restrat", "Restart sound")]
    public void AMisspelledWordStillFindsWhatItMeant(string query, string expected) => Assert.Contains(expected, Find(query));

    [Fact]
    public void LettersInOrderAndPartsOfWordsFindToo()
    {
        Assert.Contains("Restart Bluetooth", Find("bluetth"));
        Assert.Contains("Restart Bluetooth", Find("tooth"));
        Assert.Contains("Flush the DNS cache", Find("flu"));
    }

    [Fact]
    public void PluralsAndEndingsAreIgnored()
    {
        Assert.Contains("Fix printing", Find("printers"));
        Assert.Contains("Restart sound", Find("restarting"));
        Assert.Contains("Restart sound", Find("restarted"));
    }

    [Fact]
    public void ARelatedWordFindsTheThingNamedDifferently()
    {
        // Nothing here says "wifi" outside the network fix; "audio" appears nowhere at all.
        Assert.Contains("Reset the network", Find("wifi"));
        Assert.Contains("Restart sound", Find("audio"));
        Assert.Contains("Fix printing", Find("print"));
    }

    [Fact]
    public void EveryWordTypedHasToFindSomething()
    {
        Assert.Equal(["Restart Bluetooth"], Find("bluetooth headphones"));
        Assert.Empty(Find("bluetooth printer"));
        Assert.Empty(Find("zebra"));
    }

    [Fact]
    public void ShortWordsAreNotGuessedAt()
    {
        // "net" is a start of "network" but must not drift to "set"; a three letter word has to be right.
        Assert.Empty(SmartSearch.Rank(new[] { "set the clock" }, "net", x => [new(x)]));
    }

    [Fact]
    public void ANameOutranksADescriptionAndAWholePhraseOutranksScatteredWords()
    {
        var items = new[] { new Thing("Something else", "mentions the network here"), new Thing("Reset the network", "other") };
        Assert.Equal("Reset the network", SmartSearch.Rank(items, "network", x => [new(x.Name, 3), new(x.About, 1)])[0].Name);

        var phrase = new[] { new Thing("a", "cache the flush"), new Thing("b", "flush the cache") };
        Assert.Equal("b", SmartSearch.Rank(phrase, "flush the cache", x => [new(x.About)])[0].Name);
    }

    [Fact]
    public void AccentsAndCaseDoNotMatter()
    {
        Assert.Contains("Restart sound", SmartSearch.Rank(Things, "RÉSTART", x => [new(x.Name)]).Select(x => x.Name));
    }

    [Fact]
    public void TheFixCatalogIsSearchedWhateverTheTab()
    {
        var catalog = FixCatalog.Load();
        FixRowFields(catalog, out var fields);
        // One word that lives in more than one tab finds the fixes of each of them together.
        var found = SmartSearch.Rank(catalog, "windows update", fields);
        Assert.NotEmpty(found);
        Assert.NotEmpty(SmartSearch.Rank(catalog, "sfc", fields));
        Assert.Contains(catalog.First(x => x.Group == FixGroup.Network), SmartSearch.Rank(catalog, "internet", fields));
        Assert.Contains(catalog.First(x => x.Id == "rescan-devices"), SmartSearch.Rank(catalog, "devise", fields));
        // Several tabs answer together: the printer fix is on Devices and the network ones on Network.
        Assert.Contains(SmartSearch.Rank(catalog, "wifi", fields), x => x.Group == FixGroup.Network);
    }

    private static void FixRowFields(IReadOnlyList<FixItem> catalog, out Func<FixItem, IReadOnlyList<SearchField>> fields) =>
        fields = x => [new(x.Name, 4), new(x.Summary, 2), new(x.Effect, 1), new(string.Join(' ', x.Steps.Select(s => s.Title)), 1), new(string.Join(' ', x.Steps.Select(FixPlan.Describe)), 0.6)];

    [Fact]
    public void TheUnstickCatalogIsSearchedWhateverTheTab()
    {
        SearchField[] Fields(UnstickAction x) => [new(x.Name, 4), new(x.When, 2), new(x.Does, 1.2), new(string.Join(' ', x.Targets), 1)];
        var found = SmartSearch.Rank(UnstickCatalog.All, "taskbar", Fields).Select(x => x.Group).Distinct().ToList();
        Assert.True(found.Count >= 1);
        // A typo in a word from the Hardware tab still finds it while the Desktop tab would be the one open.
        Assert.Contains(SmartSearch.Rank(UnstickCatalog.All, "blutooth", Fields), x => x.Id == "restart-bluetooth");
        Assert.Contains(SmartSearch.Rank(UnstickCatalog.All, "clipbord", Fields), x => x.Id == "restart-clipboard");
        Assert.Contains(SmartSearch.Rank(UnstickCatalog.All, "wifi", Fields).Select(x => x.Group), g => g is UnstickGroup.Network or UnstickGroup.Hardware);
    }
}
