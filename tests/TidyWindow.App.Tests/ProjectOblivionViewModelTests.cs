using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TidyWindow.App.ViewModels;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class ProjectOblivionViewModelTests
{
    [Fact]
    public void ArtifactGroupMetricsTrackSelections()
    {
        var group = new ProjectOblivionArtifactGroupViewModel("Files");
        group.Items.Add(CreateArtifact("a", sizeBytes: 1_048_576, defaultSelected: true));
        group.Items.Add(CreateArtifact("b", sizeBytes: 2_097_152, defaultSelected: false));

        group.NotifySelectionMetricsChanged();

        Assert.Equal(1, group.SelectedCount);
        Assert.Equal(1_048_576, group.SelectedSizeBytes);
        Assert.False(group.AreAllSelected);

        group.SetAllSelected(true);
        Assert.Equal(2, group.SelectedCount);
        Assert.Equal(3_145_728, group.SelectedSizeBytes);
        Assert.True(group.AreAllSelected);

        group.SetAllSelected(false);
        Assert.Equal(0, group.SelectedCount);
        Assert.False(group.AreAllSelected);
    }

    [Fact]
    public void ArtifactRaisesSelectionChangedEvent()
    {
        var artifact = CreateArtifact("selection", sizeBytes: 512_000, defaultSelected: true);
        var raised = 0;
        artifact.SelectionChanged += (_, _) => raised++;

        artifact.IsSelected = false;
        artifact.IsSelected = true;

        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(524288, "0.5 MB")]
    [InlineData(5_242_880, "5 MB")]
    [InlineData(2_147_483_648, "2 GB")]
    public void FormatSizeProducesReadableLabels(long bytes, string expected)
    {
        var method = typeof(ProjectOblivionPopupViewModel).GetMethod("FormatSize", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var actual = (string)method!.Invoke(null, new object[] { bytes })!;
        Assert.Equal(expected, actual, ignoreCase: true);
    }

    private static ProjectOblivionArtifactViewModel CreateArtifact(string id, long sizeBytes, bool defaultSelected)
    {
        return new ProjectOblivionArtifactViewModel(
            artifactId: id,
            group: "Files",
            type: "Directory",
            displayName: id,
            path: $"C:/{id}",
            sizeBytes: sizeBytes,
            requiresElevation: false,
            defaultSelected: defaultSelected,
            metadata: new Dictionary<string, string>());
    }
}
