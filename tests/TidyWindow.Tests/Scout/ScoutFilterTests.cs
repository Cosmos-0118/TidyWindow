using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ScoutFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private const long MB = 1L << 20;

    private static ScoutEntry File(string name, long size = 1000, string? modified = null, string folder = @"C:\Users\me\Downloads") =>
        new(Path.Combine(folder, name), false, size, modified is null ? Now : DateTimeOffset.Parse(modified));
    private static ScoutEntry Folder(string path) => new(path, true, null, Now);
    private static string[] Find(string query, params ScoutEntry[] entries) =>
        [.. new ScoutQuery(query, null, Now).Rank(entries, ScoutItemKind.All).Select(x => x.Name)];

    [Theory]
    [InlineData("kind:document", "report.pdf", true)]
    [InlineData("kind:document", "notes.txt", true)]
    [InlineData("kind:document", "photo.jpg", false)]
    [InlineData("kind:documents", "report.docx", true)]
    [InlineData("kind:image|video", "clip.mkv", true)]
    [InlineData("kind:image|video", "song.mp3", false)]
    [InlineData("kind:spreadsheet", "budget.xlsx", true)]
    [InlineData("kind:archive", "backup.7z", true)]
    [InlineData("kind:code", "Program.cs", true)]
    [InlineData("kind:installer", "app.msi", true)]
    [InlineData("kind:installer", "VSCodeSetup-x64.exe", true)]
    [InlineData("kind:installer", "notepad.exe", false)]
    [InlineData("kind:installer", "unins000.exe", false)]
    [InlineData("kind:app", "notepad.exe", true)]
    [InlineData("kind:partial", "movie.mkv.crdownload", true)]
    [InlineData("ext:png|jpg", "a.JPG", true)]
    [InlineData("ext:.png", "a.jpg", false)]
    [InlineData("a -kind:image", "a.jpg", false)]
    [InlineData("a -kind:image", "a.txt", true)]
    public void KindsAndExtensionsMatchTheFilesTheyNameWithoutAnyWords(string query, string file, bool expected) =>
        Assert.Equal(expected, Find(query, File(file)).Length == 1);

    [Fact]
    public void FoldersAndFilesCanBeAskedForAsKinds()
    {
        var entries = new[] { File("a.txt"), Folder(@"C:\Users\me\a-folder") };
        Assert.Equal(["a-folder"], Find("kind:folder", entries));
        Assert.Equal(["a.txt"], Find("kind:file", entries));
        Assert.Equal(["a-folder"], Find("-kind:file a", entries));
        Assert.Equal(["a.png"], Find("kind:folder|image a", File("a.png")));
        Assert.Equal(["a.txt"], Find("kind:document a", entries));
    }

    [Fact]
    public void KindsCanBeNamedByAliasInSingularOrPlural()
    {
        foreach (var word in new[] { "photo", "photos", "picture", "images", "image" }) Assert.Equal(["a.png"], Find("kind:" + word, File("a.png")));
        Assert.Equal(["a.mp3"], Find("kind:music", File("a.mp3")));
        Assert.Equal(["a.xlsx"], Find("kind:excel", File("a.xlsx")));
    }

    [Fact]
    public void SizeFiltersUnderstandComparisonsRangesAndNamedSizes()
    {
        var entries = new[] { File("empty.bin", 0), File("tiny.bin", 2000), File("small.bin", 500_000), File("medium.bin", 5 * MB), File("big.bin", 200 * MB), File("huge.bin", 3L << 30) };
        Assert.Equal(["big.bin", "huge.bin"], Find("size:>100mb", entries).Order().ToArray());
        Assert.Equal(["empty.bin", "tiny.bin"], Find("size:<=10kb", entries).Order().ToArray());
        Assert.Equal(["medium.bin"], Find("size:1mb..10mb", entries));
        Assert.Equal(["empty.bin"], Find("size:empty", entries));
        Assert.Equal(["huge.bin"], Find("size:huge", entries));
        Assert.Equal(["medium.bin"], Find("size:medium", entries));
        Assert.Equal(["small.bin"], Find("size:small", entries));
        Assert.Equal(["tiny.bin"], Find("size:tiny", entries));
        Assert.Equal(["empty.bin", "medium.bin", "small.bin", "tiny.bin"], Find("bin -size:>100mb", entries).Order().ToArray());
    }

    [Fact]
    public void FoldersNeverPassASizeFilter() => Assert.Empty(Find("size:>0", Folder(@"C:\x\y")));

    [Fact]
    public void DateFiltersUnderstandNamedPeriodsRelativeSpansAndDays()
    {
        var entries = new[] { File("today.txt", modified: "2026-11-03T12:00:00+00:00"), File("lastweek.txt", modified: "2026-10-30T12:00:00+00:00"), File("lastyear.txt", modified: "2025-06-15T08:00:00+00:00") };
        Assert.Equal(["today.txt"], Find("date:today", entries));
        Assert.Equal(["lastweek.txt", "today.txt"], Find("date:7d", entries).Order().ToArray());
        Assert.Equal(["lastyear.txt"], Find("date:2025", entries));
        Assert.Equal(["lastyear.txt"], Find("before:2026-01-01", entries));
        Assert.Equal(["lastweek.txt", "today.txt"], Find("after:2026-10-01", entries).Order().ToArray());
        Assert.Equal(["lastyear.txt"], Find("before:1y", entries.Concat([File("recent.txt", modified: "2026-08-01T00:00:00+00:00")]).ToArray()));
    }

    [Fact]
    public void FilesWithoutADateOrSizeDoNotPassFiltersOnThem()
    {
        Assert.Empty(new ScoutQuery("date:today", null, Now).Rank([new ScoutEntry(@"C:\a.txt", false, 5, null)], ScoutItemKind.All));
        Assert.Empty(new ScoutQuery("size:>1b", null, Now).Rank([new ScoutEntry(@"C:\a.txt", false, null, Now)], ScoutItemKind.All));
    }

    [Fact]
    public void FolderAndNameFiltersLookWhereTheyAreToldTo()
    {
        var entries = new[] { File("setup.exe", folder: @"C:\Users\me\Downloads"), File("setup.exe", folder: @"C:\Users\me\Desktop"), File("readme.md", folder: @"C:\Users\me\Downloads\setup-files") };
        Assert.Equal(["setup.exe", "readme.md"], Find("setup in:downloads", entries));
        Assert.Equal(2, Find("in:downloads", entries).Length);
        Assert.Equal(2, Find("in:downloads|desktop name:setup", entries).Length);
        Assert.Equal(["readme.md"], Find("in:downloads -name:setup.exe", entries));
        Assert.Equal(["setup.exe"], Find("name:setup.exe in:desktop", entries));
    }

    [Fact]
    public void LeftOutWordsRemoveWhateverHasThem()
    {
        var entries = new[] { File("report-final.pdf"), File("report-draft.pdf"), File("notes.pdf", folder: @"C:\Users\me\draft-box") };
        Assert.Equal(["report-final.pdf"], Find("report -draft", entries));
        Assert.Equal(["report-final.pdf"], Find("pdf -\"draft\"", entries));
    }

    [Fact]
    public void ResultsFollowTheChosenSortAndFilterOnlySearchesAreNewestFirst()
    {
        var entries = new[] { File("a.txt", 30, "2026-01-01T00:00:00+00:00"), File("b.txt", 10, "2026-03-01T00:00:00+00:00"), File("c.txt", 20, "2026-02-01T00:00:00+00:00") };
        Assert.Equal(["b.txt", "c.txt", "a.txt"], Find("kind:document", entries));
        Assert.Equal(["a.txt", "c.txt", "b.txt"], Find("kind:document sort:oldest", entries));
        Assert.Equal(["a.txt", "c.txt", "b.txt"], Find("kind:document sort:largest", entries));
        Assert.Equal(["b.txt", "c.txt", "a.txt"], Find("kind:document sort:smallest", entries));
        Assert.Equal(["a.txt", "b.txt", "c.txt"], Find("txt sort:name", entries));
    }

    [Fact]
    public void FiltersCombineAndEveryOneMustHold()
    {
        var entries = new[] { File("a.pdf", 5 * MB, "2026-11-02T00:00:00+00:00"), File("b.pdf", 5 * MB, "2025-01-01T00:00:00+00:00"), File("c.pdf", 100, "2026-11-02T00:00:00+00:00"), File("d.png", 5 * MB, "2026-11-02T00:00:00+00:00") };
        Assert.Equal(["a.pdf"], Find("kind:pdf size:>1mb date:week", entries));
    }

    [Fact]
    public void UnknownFiltersAreSearchedForAsTextNeverIgnored()
    {
        var query = new ScoutQuery("kind:unicorn report", null, Now);
        Assert.Equal(["kind:unicorn", "report"], query.Terms);
        Assert.True(query.Filter.IsEmpty);
        Assert.Equal(["size:enormous"], new ScoutQuery("size:enormous", null, Now).Terms);
        Assert.Equal(["c:\\users\\me"], new ScoutQuery(@"C:\Users\Me", null, Now).Terms);
    }

    [Fact]
    public void ASearchNeedsAWordOrAFilterThatAsksForSomething()
    {
        Assert.Throws<ArgumentException>(() => new ScoutQuery("-draft", null, Now));
        Assert.Throws<ArgumentException>(() => new ScoutQuery("sort:newest", null, Now));
        Assert.Empty(new ScoutQuery("kind:image", null, Now).Terms);
        Assert.NotNull(new ScoutQuery("size:>1gb", null, Now));
    }

    [Fact]
    public void ChipsDescribeWhatWasUnderstood()
    {
        var chips = ScoutFilter.Parse("report kind:pdf|image size:>100mb -draft date:7d in:downloads sort:newest", Now).Chips;
        Assert.Contains("Type: pdfs or images", chips);
        Assert.Contains("Size at least 100 MB", chips);
        Assert.Contains("Without “draft”", chips);
        Assert.Contains("Modified: the last 7 days", chips);
        Assert.Contains("In: “downloads”", chips);
        Assert.Contains("Sorted by newest", chips);
    }

    [Fact]
    public void IndexExpressionsCarryTheFiltersAsNativeTermsBuiltOnlyFromValidatedValues()
    {
        var expression = new ScoutQuery("kind:image size:>1gb report", null, Now).NameExpression(ScoutItemKind.All, @"C:\Users\me");
        Assert.Contains(" ext:", expression);
        Assert.Contains("png", expression);
        Assert.Contains("size:>=1073741825", expression);
        Assert.Contains("nowildcards:\"report\"", expression);
        Assert.EndsWith("\"C:\\Users\\me\\\"", expression);

        Assert.Equal("folder:", new ScoutQuery("kind:folder", null, Now).NameExpression(ScoutItemKind.All, null));
        Assert.Contains("!ext:png", new ScoutQuery("-ext:png report", null, Now).NameExpression(ScoutItemKind.All, null));

        // Typed text never reaches Everything as an operator: an extension with odd characters is not a filter at all.
        var odd = new ScoutQuery("ext:png;!x report", null, Now);
        Assert.DoesNotContain(" ext:", odd.NameExpression(ScoutItemKind.All, null));
        Assert.DoesNotContain("!ext:", odd.NameExpression(ScoutItemKind.All, null));
        Assert.Contains("nowildcards:\"ext:png;!x\"", odd.NameExpression(ScoutItemKind.All, null));
    }

    [Fact]
    public void FolderFiltersSearchWholePathsAndNeverNeedTheNameToMatch()
    {
        var query = new ScoutQuery("kind:installer in:downloads|desktop", null, Now);
        Assert.True(query.NeedsPathMatch);
        Assert.Contains("<nowildcards:\"downloads\"|nowildcards:\"desktop\">", query.NameExpression(ScoutItemKind.All, null));
        Assert.False(new ScoutQuery("kind:installer", null, Now).NeedsPathMatch);
    }

    [Fact]
    public void FilterOnlySearchesAskTheIndexForTheNewestFirstAndSkipNearMatches()
    {
        Assert.Equal(14u, new ScoutQuery("kind:installer", null, Now).NativeSort);
        Assert.Equal(1u, new ScoutQuery("report", null, Now).NativeSort);
        Assert.Equal(6u, new ScoutQuery("report sort:largest", null, Now).NativeSort);
        Assert.Equal(14u, new ScoutQuery("report date:today", null, Now).NativeSort);
        Assert.Null(new ScoutQuery("kind:installer", null, Now).SmartExpression(ScoutItemKind.All, null));
        Assert.Null(new ScoutQuery("kind:installer", null, Now).ExactExpression(ScoutItemKind.All));
    }

    [Fact]
    public void EveryKindHasAUniqueKeyAndAliasesAndOnlyPlainExtensions()
    {
        Assert.True(ScoutCategories.All.Count >= 25);
        Assert.Equal(ScoutCategories.All.Count, ScoutCategories.All.Select(x => x.Key).Distinct().Count());
        foreach (var category in ScoutCategories.All)
        {
            Assert.Same(category, ScoutCategories.Find(category.Key));
            Assert.All(category.PossibleExtensions, x => Assert.Matches("^[a-z0-9]{1,12}$", x));
        }
        var aliases = ScoutCategories.All.SelectMany(c => c.Aliases.Select(a => (c.Key, Alias: a))).ToArray();
        Assert.All(aliases.GroupBy(x => x.Alias), g => Assert.Single(g.Select(x => x.Key).Distinct()));
    }
}
