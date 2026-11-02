using System.Globalization;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ArchiveSearchTests
{
    // Monday 2 November 2026, 15:00, in whatever time zone this PC is in.
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, DateTimeOffset.Now.Offset);

    private static ArchiveEntry Entry(string title = "Repaired 50 leftovers", string names = "CMake; Rust", string paths = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\CMake", DateTimeOffset? when = null,
        ArchiveKinds kinds = ArchiveKinds.Shortcut, long bytes = -1, bool admin = false) => new(title, names, paths, when ?? Now.AddHours(-1), kinds, bytes, admin);

    private static bool Hits(string query, ArchiveEntry entry) => ArchiveQuery.Parse(query, Now).Matches(entry);

    [Fact]
    public void AnEmptySearchMatchesEverythingAndSaysNothing()
    {
        var query = ArchiveQuery.Parse("   ", Now);
        Assert.True(query.IsEmpty);
        Assert.True(query.Matches(Entry()));
        Assert.Empty(query.Chips);
    }

    [Theory]
    [InlineData("cmake", true)]
    [InlineData("CMAKE", true)]
    [InlineData("cmake rust", true)]
    [InlineData("cmake python", false)]
    [InlineData("\"start menu\"", true)]
    [InlineData("\"menu start\"", false)]
    [InlineData("-python", true)]
    [InlineData("-cmake", false)]
    [InlineData("cmake -rust", false)]
    [InlineData("programdata", true)]
    [InlineData("shortcut", true)]
    [InlineData("november", true)]
    [InlineData("monday", true)]
    [InlineData("2026-11-02", true)]
    public void PlainWordsAllHaveToBeFoundAndADashLeavesOutWhatHasThem(string query, bool expected) => Assert.Equal(expected, Hits(query, Entry()));

    [Theory]
    [InlineData("kind:shortcut", true)]
    [InlineData("kind:folder", false)]
    [InlineData("kind:folder|shortcut", true)]
    [InlineData("type:links", true)]
    [InlineData("-kind:shortcut", false)]
    [InlineData("-kind:folder", true)]
    [InlineData("repair:leftovers", true)]
    [InlineData("repair:cmake", false)]
    [InlineData("name:rust", true)]
    [InlineData("name:programdata", false)]
    [InlineData("path:programdata", true)]
    [InlineData("path:cmake|python", true)]
    [InlineData("admin:no", true)]
    [InlineData("admin:yes", false)]
    [InlineData("repair:\"50 leftovers\"", true)]
    public void FiltersNarrowTheSearchToOneThing(string query, bool expected) => Assert.Equal(expected, Hits(query, Entry()));

    [Theory]
    [InlineData("date:today", 0, true)]
    [InlineData("date:yesterday", 0, false)]
    [InlineData("date:yesterday", -24, true)]
    [InlineData("date:week", -24 * 6, true)]
    [InlineData("date:week", -24 * 8, false)]
    [InlineData("date:month", -24 * 20, true)]
    [InlineData("date:month", -24 * 40, false)]
    [InlineData("date:2026-11-02", 0, true)]
    [InlineData("date:2026-11", -24, true)]
    [InlineData("date:2026-10", -24 * 5, true)]
    [InlineData("date:2026", -24 * 200, true)]
    [InlineData("date:2025", 0, false)]
    [InlineData("after:2026-11-01", 0, true)]
    [InlineData("after:2026-11-03", 0, false)]
    [InlineData("before:2026-11-03", 0, true)]
    [InlineData("before:2026-11-02", 0, false)]
    [InlineData("after:2026-10 before:2026-11", -24 * 5, true)]
    [InlineData("after:2026-10 before:2026-11", 0, false)]
    [InlineData("-date:today", 0, false)]
    public void DatesAreFoundByDayMonthYearAndNamedPeriods(string query, int hoursAgo, bool expected) =>
        Assert.Equal(expected, Hits(query, Entry(when: Now.AddHours(hoursAgo))));

    [Theory]
    [InlineData("size:>1mb", 5L << 20, true)]
    [InlineData("size:>10mb", 5L << 20, false)]
    [InlineData("size:<10mb", 5L << 20, true)]
    [InlineData("size:>=5mb", 5L << 20, true)]
    [InlineData("size:=5mb", 5L << 20, true)]
    [InlineData("size:>1.5kb", 2048, true)]
    [InlineData("size:>1gb", 5L << 20, false)]
    [InlineData("size:>1mb", -1, false)]
    [InlineData("-size:>1mb", 5L << 20, false)]
    public void SizesNeedAnOperatorAndAreNeverGuessedWhenUnknown(string query, long bytes, bool expected) => Assert.Equal(expected, Hits(query, Entry(bytes: bytes)));

    [Theory]
    [InlineData("kind:nonsense")]
    [InlineData("date:someday")]
    [InlineData("size:big")]
    [InlineData("admin:maybe")]
    [InlineData("colour:red")]
    public void AFilterItDoesNotUnderstandIsSearchedForAsTextSoNothingMatchesEverything(string query)
    {
        Assert.False(Hits(query, Entry()));
        Assert.Contains(query, Assert.Single(ArchiveQuery.Parse(query, Now).Chips), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSearchSaysHowItWasRead()
    {
        var chips = ArchiveQuery.Parse("ghost kind:folder|shortcut after:2026-10-01 size:>100mb -temp admin:yes", Now).Chips;
        Assert.Equal(["Has “ghost”", "Type: shortcut or folder", "After " + new DateTime(2026, 10, 1).ToString("d MMM yyyy", CultureInfo.CurrentCulture), "Size > 100 MB", "Without “temp”", "Needs admin"], chips.ToList());
    }

    [Fact]
    public void SeveralFiltersAllHaveToHold()
    {
        var entry = Entry(kinds: ArchiveKinds.Folder | ArchiveKinds.Shortcut, bytes: 300L << 20, admin: true);
        Assert.True(Hits("kind:folder admin:yes size:>100mb cmake date:today", entry));
        Assert.False(Hits("kind:folder admin:yes size:>100mb cmake date:yesterday", entry));
        Assert.True(Hits("kind:registry|folder", entry));
    }

    // ---- Dates in the lists ----

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(-24, "Yesterday")]
    public void RecentDaysAreNamed(int hoursAgo, string label) => Assert.Equal(label, ArchiveDates.Bucket(Now.AddHours(hoursAgo), Now).Label);

    [Fact]
    public void TheWeekMonthAndYearsFollowTheCalendarNotAFixedNumberOfDays()
    {
        // Now is Monday, so nothing earlier in the same week exists yet, and last week starts a week before.
        Assert.Equal((3, "Last week"), ArchiveDates.Bucket(new(2026, 10, 28, 9, 0, 0, Now.Offset), Now));
        Assert.Equal((4, "Earlier this month"), ArchiveDates.Bucket(new(2026, 11, 1, 9, 0, 0, Now.Offset), new(2026, 11, 20, 9, 0, 0, Now.Offset)));
        var thursday = new DateTimeOffset(2026, 11, 5, 15, 0, 0, Now.Offset);
        Assert.Equal((2, "Earlier this week"), ArchiveDates.Bucket(new(2026, 11, 2, 9, 0, 0, Now.Offset), thursday));
        Assert.Equal((3, "Last week"), ArchiveDates.Bucket(new(2026, 10, 27, 9, 0, 0, Now.Offset), thursday));
        var october = ArchiveDates.Bucket(new(2026, 10, 5, 9, 0, 0, Now.Offset), Now);
        Assert.Equal(101, october.Order);
        Assert.Equal(new DateTime(2026, 10, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture), october.Label);
        Assert.Equal((1001, "2025"), ArchiveDates.Bucket(new(2025, 1, 5, 9, 0, 0, Now.Offset), Now));
        Assert.Equal((1003, "2023"), ArchiveDates.Bucket(new(2023, 6, 5, 9, 0, 0, Now.Offset), Now));
    }

    [Fact]
    public void GroupsSortNewestFirstAndAClockAheadOfTheFutureIsToday()
    {
        var days = new[] { Now.AddDays(-400), Now.AddDays(-40), Now.AddDays(-9), Now.AddDays(-1), Now, Now.AddDays(3) };
        var order = days.Select(x => ArchiveDates.Bucket(x, Now).Order).ToList();
        Assert.Equal(order.OrderByDescending(x => x == 0 ? int.MinValue : x).Reverse().Count(), order.Count);
        Assert.Equal(0, ArchiveDates.Bucket(Now.AddDays(3), Now).Order);
        Assert.True(ArchiveDates.Bucket(Now.AddDays(-400), Now).Order > ArchiveDates.Bucket(Now.AddDays(-40), Now).Order);
        Assert.True(ArchiveDates.Bucket(Now.AddDays(-40), Now).Order > ArchiveDates.Bucket(Now.AddDays(-9), Now).Order);
    }

    [Fact]
    public void ThirtyThousandEntriesAreSearchedInWellUnderASecondEvenWithSeveralFilters()
    {
        var entries = Enumerable.Range(0, 30000).Select(i => Entry(title: "Repaired " + i % 97, names: "Name" + i, paths: @"C:\ProgramData\Package Cache\{" + i + "}", when: Now.AddHours(-i), kinds: i % 2 == 0 ? ArchiveKinds.Folder : ArchiveKinds.Shortcut, bytes: i * 1024L)).ToList();
        var query = ArchiveQuery.Parse("kind:folder date:month size:>1kb -name1 path:package \"repaired 5\"", Now);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = entries.Count(query.Matches);
        clock.Stop();

        Assert.True(found > 0);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"{clock.ElapsedMilliseconds} ms");
    }
}
