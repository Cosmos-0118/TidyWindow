using System;
using System.Collections.Immutable;
using System.Linq;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Maintenance;
using Xunit;

namespace TidyWindow.Core.Tests;

public sealed class RegistryOptimizerConfigurationTests
{
    private static RegistryOptimizerService CreateService() => new(new PowerShellInvoker());

    [Fact]
    public void PresetsCoverAllTweaks()
    {
        var service = CreateService();
        var tweakIds = service.Tweaks.Select(t => t.Id).ToImmutableArray();

        Assert.True(tweakIds.Length >= 40, $"Expected at least 40 registry tweaks, found {tweakIds.Length}.");

        foreach (var preset in service.Presets)
        {
            var missing = tweakIds.Where(id => !preset.States.ContainsKey(id)).ToImmutableArray();
            Assert.True(missing.Length == 0, $"Preset '{preset.Id}' missing states: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void TweaksSupportEnableAndDisablePaths()
    {
        var service = CreateService();
        var missing = service.Tweaks
            .Where(tweak => tweak.ResolveOperation(true) is null || tweak.ResolveOperation(false) is null)
            .Select(tweak => tweak.Id)
            .ToImmutableArray();

        Assert.True(missing.Length == 0, $"Tweaks missing enable/disable operations: {string.Join(", ", missing)}");
    }

    [Fact]
    public void GamingPresetBuildPlanCoversAllChangedTweaks()
    {
        var service = CreateService();
        var gaming = service.Presets.Single(preset => string.Equals(preset.Id, "gaming", StringComparison.OrdinalIgnoreCase));

        var selections = service.Tweaks
            .Select(tweak =>
            {
                var target = gaming.TryGetState(tweak.Id, out var state) ? state : tweak.DefaultState;
                return new RegistrySelection(tweak.Id, target, tweak.DefaultState);
            })
            .Where(selection => selection.TargetState != selection.PreviousState)
            .ToImmutableArray();

        var plan = service.BuildPlan(selections);
        var expected = selections.Count(selection => service.GetTweak(selection.TweakId).ResolveOperation(selection.TargetState) is not null);

        Assert.Equal(expected, plan.ApplyOperations.Length);
        Assert.True(expected > 0, "Gaming preset should change at least one tweak from defaults.");
    }
}
