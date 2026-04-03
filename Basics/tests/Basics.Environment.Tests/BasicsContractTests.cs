using Nbn.Demos.Basics.Environment;
using Nbn.Shared.Format;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsContractTests
{
    [Fact]
    public void DefaultSeedTemplate_IsTemplateAnchored_AndTwoByTwo()
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
        Assert.Contains(validation.Errors, error => error.Contains("2->2", StringComparison.Ordinal));
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

    [Fact]
    public void ExecutionStopCriteria_CanRequireBothOrEitherThreshold()
    {
        var requireBoth = new BasicsExecutionStopCriteria
        {
            TargetAccuracy = 0.8f,
            TargetFitness = 0.9f,
            RequireBothTargets = true
        };
        var requireEither = requireBoth with { RequireBothTargets = false };

        Assert.False(requireBoth.IsSatisfied(0.85f, 0.2f));
        Assert.True(requireBoth.IsSatisfied(0.85f, 0.95f));
        Assert.True(requireEither.IsSatisfied(0.85f, 0.2f));
        Assert.True(requireEither.IsSatisfied(0.2f, 0.95f));
        Assert.False(requireEither.IsSatisfied(0.2f, 0.3f));
    }

    [Fact]
    public void MultiplicationTaskSettings_RejectToleranceAboveAdjacentInputDelta()
    {
        var settings = new BasicsTaskSettings
        {
            Multiplication = new BasicsMultiplicationTaskSettings
            {
                UniqueInputValueCount = 3,
                AccuracyTolerance = 0.5001f
            }
        };

        var validation = settings.ValidateForTask("multiplication");

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("adjacent input delta (0.5)", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiplicationTaskSettings_AllowsToleranceAtAdjacentInputDeltaBoundary()
    {
        var settings = new BasicsTaskSettings
        {
            Multiplication = new BasicsMultiplicationTaskSettings
            {
                UniqueInputValueCount = 3,
                AccuracyTolerance = 0.5f
            }
        };

        var validation = settings.ValidateForTask("multiplication");

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void OutputSamplingPolicy_RejectsReadyWindowsBelowOne()
    {
        var policy = new BasicsOutputSamplingPolicy
        {
            MaxReadyWindowTicks = 0
        };

        var validation = policy.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("Ready window ticks", StringComparison.Ordinal));
    }

    [Fact]
    public void OutputSamplingPolicy_RejectsSampleRepeatCountsAboveMaximum()
    {
        var policy = new BasicsOutputSamplingPolicy
        {
            SampleRepeatCount = BasicsOutputSamplingPolicy.MaximumSampleRepeatCount + 1
        };

        var validation = policy.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("Sample repeat count", StringComparison.Ordinal));
    }

    [Fact]
    public void OutputSamplingPolicy_DefaultsToTwoSampleRepeats()
    {
        var policy = new BasicsOutputSamplingPolicy();

        Assert.Equal(2, policy.SampleRepeatCount);
    }

    [Fact]
    public void InitialBrainSeedValidation_RejectsLegacyGeometryOutsideUiImportPath()
    {
        var legacyBytes = DemoNbnBuilder.BuildSampleNbn();
        var legacyAnalysis = BasicsDefinitionAnalyzer.Analyze(legacyBytes);
        var seed = new BasicsInitialBrainSeed(
            DisplayName: "legacy-demo",
            DefinitionBytes: legacyBytes,
            DuplicateForReproduction: false,
            Complexity: legacyAnalysis.Complexity);

        var validation = seed.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("geometry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, error => error.Contains("expected_2x2", StringComparison.Ordinal));
    }
}
