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

    [Fact]
    public void SeedShapeConstraints_RejectInvalidRanges()
    {
        var template = BasicsSeedTemplateContract.CreateDefault() with
        {
            InitialSeedShapeConstraints = new BasicsSeedShapeConstraints
            {
                MinActiveInternalRegionCount = 4,
                MaxActiveInternalRegionCount = 2,
                MinInternalNeuronCount = -1,
                MinAxonCount = 10,
                MaxAxonCount = 9
            }
        };

        var validation = template.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("Active internal region count maximum", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("Internal neuron count minimum", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("Axon count maximum", StringComparison.Ordinal));
    }

    [Fact]
    public void DiversityTuning_HighScheduling_MatchesCurrentMultiplicationSessionDefaults()
    {
        var scheduling = BasicsDiversityTuning.CreateScheduling(BasicsDiversityPreset.High);

        Assert.Equal(0.50d, scheduling.ParentSelection.FitnessWeight);
        Assert.Equal(0.43d, scheduling.ParentSelection.DiversityWeight);
        Assert.Equal(0.15d, scheduling.ParentSelection.SpeciesBalanceWeight);
        Assert.Equal(0.25d, scheduling.ParentSelection.EliteFraction);
        Assert.Equal(0.53d, scheduling.ParentSelection.ExplorationFraction);
        Assert.Equal(8, scheduling.ParentSelection.MaxParentsPerSpecies);
        Assert.Equal((uint)3, scheduling.RunAllocation.MinRunsPerPair);
        Assert.Equal((uint)8, scheduling.RunAllocation.MaxRunsPerPair);
        Assert.Equal(1.10d, scheduling.RunAllocation.FitnessExponent);
        Assert.Equal(0.60d, scheduling.RunAllocation.DiversityBoost);
    }

    [Fact]
    public void ExecutionStopCriteria_DefaultsToUnlimitedGenerations()
    {
        var criteria = new BasicsExecutionStopCriteria();

        var validation = criteria.Validate();

        Assert.True(validation.IsValid);
        Assert.Null(criteria.MaximumGenerations);
        Assert.False(criteria.IsGenerationLimitReached(64));
    }
}
