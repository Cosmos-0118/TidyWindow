using System.Collections.Immutable;
using TidyWindow.App.ViewModels;
using TidyWindow.Core.Install;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class InstallPackageItemViewModelSearchTests
{
    [Fact]
    public void TryMatch_FieldTokensRequireAllTerms()
    {
        var vm = CreateVm(
            id: "Microsoft.WindowsTerminal",
            name: "Windows Terminal",
            manager: "winget",
            summary: "Modern terminal app for power users",
            tags: new[] { "terminal", "cli", "productivity" });

        Assert.True(vm.TryMatch("manager:winget tag:terminal", out _));
        Assert.False(vm.TryMatch("manager:scoop tag:terminal", out _));
    }

    [Fact]
    public void TryMatch_QuotedPhraseMatchesName()
    {
        var vm = CreateVm(
            id: "Microsoft.VisualStudioCode",
            name: "Visual Studio Code",
            manager: "winget",
            summary: "Code editor",
            tags: new[] { "editor", "ide" });

        Assert.True(vm.TryMatch("\"studio code\"", out _));
    }

    [Fact]
    public void TryMatch_FuzzyHandlesSingleTransposition()
    {
        var vm = CreateVm(
            id: "Microsoft.VisualStudioCode",
            name: "Visual Studio Code",
            manager: "winget",
            summary: "Code editor",
            tags: new[] { "editor", "ide" });

        Assert.True(vm.TryMatch("vsiual", out _));
    }

    [Fact]
    public void TryMatch_NamePrefixScoresHigherThanSummaryContains()
    {
        var prefixMatch = CreateVm(
            id: "Contoso.TerminalTools",
            name: "Terminal Toolkit",
            manager: "winget",
            summary: "Developer tools",
            tags: new[] { "cli" });

        var summaryMatch = CreateVm(
            id: "Contoso.DevTools",
            name: "Developer Toolbox",
            manager: "winget",
            summary: "Includes terminal scripts for maintenance",
            tags: new[] { "tools" });

        Assert.True(prefixMatch.TryMatch("term", out var prefixScore));
        Assert.True(summaryMatch.TryMatch("term", out var summaryScore));
        Assert.True(prefixScore > summaryScore);
    }

    private static InstallPackageItemViewModel CreateVm(string id, string name, string manager, string summary, string[] tags)
    {
        var definition = new InstallPackageDefinition(
            Id: id,
            Name: name,
            Manager: manager,
            Command: "install",
            RequiresAdmin: false,
            Summary: summary,
            Homepage: null,
            Tags: tags.ToImmutableArray(),
            Buckets: ImmutableArray<string>.Empty);

        return new InstallPackageItemViewModel(definition);
    }
}
