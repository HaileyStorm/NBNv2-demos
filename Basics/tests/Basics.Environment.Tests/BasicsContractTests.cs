using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsContractTests
{
    [Fact]
    public void DefaultSeedTemplate_IsTemplateAnchored_AndTwoByOne()
    {
        var template = BasicsSeedTemplateContract.CreateDefault();

        var validation = template.Validate();

        Assert.True(validation.IsValid);
        Assert.Equal(BasicsIoGeometry.InputWidth, template.InputWidth);
        Assert.Equal(BasicsIoGeometry.OutputWidth, template.OutputWidth);
        Assert.False(template.AllowOffTemplateSeeds);
        Assert.True(template.ExpectSingleBootstrapSpecies);
    }

    [Fact]
    public void SeedTemplateValidation_RejectsOffTemplateBootstrapFlags()
    {
        var template = BasicsSeedTemplateContract.CreateDefault() with
        {
            AllowOffTemplateSeeds = true,
            ExpectSingleBootstrapSpecies = false,
            InputWidth = 3
        };

        var validation = template.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("template-anchored", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("SingleBootstrapSpecies", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("2->1", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultReproductionPolicy_KeepsIoNeuronProtectionEnabled()
    {
        var policy = BasicsReproductionPolicy.CreateDefault();

        var validation = policy.Validate();

        Assert.True(validation.IsValid);
        Assert.True(policy.Config.HasProtectIoRegionNeuronCounts);
        Assert.True(policy.Config.ProtectIoRegionNeuronCounts);
    }

    [Fact]
    public void ReproductionBudgetPlanner_CombinesFitnessAndDiversityWithinCapacityBounds()
    {
        var policy = BasicsReproductionSchedulingPolicy.Default;

        var parentScore = BasicsReproductionBudgetPlanner.ScoreParentCandidate(
            policy.ParentSelection,
            normalizedFitness: 0.8f,
            normalizedNovelty: 0.6f,
            normalizedSpeciesBalance: 0.5f);
        var runCount = BasicsReproductionBudgetPlanner.ResolveRunCount(
            policy.RunAllocation,
            capacityBound: 4,
            normalizedFitness: 1f,
            normalizedNovelty: 1f);

        Assert.InRange(parentScore.WeightedScore, 0.6f, 0.7f);
        Assert.Equal(4u, runCount);
    }
}
