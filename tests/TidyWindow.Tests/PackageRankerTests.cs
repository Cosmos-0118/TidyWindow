using System.Diagnostics;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class PackageRankerTests(ITestOutputHelper output)
{
    private static Package App(string name, string id, ManagerKind manager = ManagerKind.Winget) => new(manager, id, name, "1.0", "source");

    [Fact]
    public void ExactProductBeatsAlphabeticalPrefixesAndPublisherMatches()
    {
        var git = App("Git", "Git.Git");
        var candidates = new[] { App("A Git Tutorial", "Docs.Tutorial"), App("GitHub Desktop", "GitHub.Desktop"), App("Git Extensions", "Tools.Extensions"), App("A tool", "Git.Tools"), git };
        var ranked = PackageRanker.Rank(candidates, "git");
        Assert.Equal(git, ranked[0]);
        Assert.True(Array.IndexOf(ranked.ToArray(), candidates[2]) < Array.IndexOf(ranked.ToArray(), candidates[1]));
        Assert.Equal(candidates.Length, ranked.Count);
    }

    [Fact]
    public void ExplicitIdentifierIsFirstAndInstallIdentityIsUnchanged()
    {
        var exact = App("Microsoft Visual Studio Code", "Microsoft.VisualStudioCode");
        var other = App("Microsoft VisualStudioCode Extension", "Other.Extension");
        Assert.Same(exact, PackageRanker.Rank([other, exact], "Microsoft.VisualStudioCode")[0]);
        Assert.Equal("Microsoft.VisualStudioCode", exact.Id);
    }

    [Fact]
    public void MultiwordCoverageBeatsRepeatedSingleTerm()
    {
        var editor = App("Visual Studio Code", "Microsoft.VisualStudioCode");
        var partial = App("Visual Visual Visual Visual Tools", "Tools.Visual");
        Assert.Equal(editor, PackageRanker.Rank([partial, editor], "visual code")[0]);
    }

    [Fact]
    public void AdjacentTranspositionBeatsUnrelatedBackendMatch()
    {
        var python = App("Python", "Python.Python.3");
        Assert.Equal(python, PackageRanker.Rank([App("A runtime", "Vendor.Runtime"), python], "pyhton")[0]);
    }

    [Theory]
    [InlineData("Cafe", "Café")]
    [InlineData("visual studio code", "VisualStudioCode")]
    [InlineData("Ｃａｆｅ", "Café")]
    [InlineData("编辑器", "编辑器")]
    public void UnicodeAndWordBoundariesAreNormalized(string query, string name)
    {
        var exact = App(name, "Vendor.Tool");
        Assert.Equal(exact, PackageRanker.Rank([App("Another app", "Vendor.Other"), exact], query)[0]);
    }

    [Fact]
    public void ProgrammingLanguageSymbolsKeepTheirMeaning()
    {
        var cpp = App("C++", "Vendor.Cpp");
        var csharp = App("C#", "Vendor.Csharp");
        Assert.Equal(cpp, PackageRanker.Rank([csharp, cpp], "C++")[0]);
        Assert.Equal(csharp, PackageRanker.Rank([cpp, csharp], "C#")[0]);
    }

    [Fact]
    public void SourceChoicesRemainAndTiesAreIndependentOfArrivalOrder()
    {
        var candidates = new[] { App("Git", "git", ManagerKind.Chocolatey), App("Git", "main/git", ManagerKind.Scoop), App("Git", "Git.Git") };
        var ranked = PackageRanker.Rank(candidates.Concat([candidates[0]]), "git");
        Assert.Equal(3, ranked.Count);
        Assert.Equal(ranked, PackageRanker.Rank(candidates.Reverse(), "git"));
    }

    [Fact]
    public void FullCandidateSetRankingLatencyIsMeasured()
    {
        var candidates = Enumerable.Range(0, 180).Select(i => App($"Visual Studio Tool {i}", $"Vendor.Tool{i}"))
            .Append(App("Visual Studio Code", "Microsoft.VisualStudioCode")).ToArray();
        PackageRanker.Rank(candidates, "visual studio code"); // Exclude first JIT/regex initialization.
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) Assert.Equal("Microsoft.VisualStudioCode", PackageRanker.Rank(candidates, "visual studio code")[0].Id);
        output.WriteLine($"Mean ranking time for {candidates.Length} candidates: {timer.Elapsed.TotalMilliseconds / 100:F2} ms");
    }
}
