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
            baseRunCount: 4,
            normalizedFitness: 1f,
            normalizedNovelty: 1f);

        Assert.InRange(parentScore.WeightedScore, 0.6f, 0.7f);
        Assert.Equal(6u, runCount);
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
    public void DiversityTuning_AdaptiveBoost_WidensLowVariationBand()
    {
        var low = BasicsDiversityTuning.CreateVariationBand(BasicsDiversityPreset.Low);

        var boosted = BasicsDiversityTuning.ResolveEffectiveVariationBand(
            low,
            BasicsDiversityPreset.Low,
            boostSteps: 3);

        Assert.Equal(4, boosted.MaxInternalNeuronDelta);
        Assert.Equal(14, boosted.MaxAxonDelta);
        Assert.Equal(8, boosted.MaxStrengthCodeDelta);
        Assert.Equal(8, boosted.MaxParameterCodeDelta);
        Assert.True(boosted.AllowFunctionMutation);
        Assert.True(boosted.AllowAxonReroute);
        Assert.False(boosted.AllowRegionSetChange);
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
    public void MultiplicationTaskSettings_ValidatesBehaviorOccupancyRamp()
    {
        var settings = new BasicsTaskSettings
        {
            Multiplication = new BasicsMultiplicationTaskSettings
            {
                BehaviorStageGateStart = 0.60f,
                BehaviorStageGateFull = 0.50f
            }
        };

        var validation = settings.ValidateForTask("multiplication");

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("behavior ramp full", StringComparison.Ordinal));
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
    public void OutputSamplingPolicy_DefaultsToSingleSampleRepeat()
    {
        var policy = new BasicsOutputSamplingPolicy();

        Assert.Equal(1, policy.SampleRepeatCount);
    }

    [Fact]
    public void ReproductionBudgetPlanner_UsesBaseRunCountAsBaselineWithinMinMax()
    {
        var policy = new BasicsRunAllocationPolicy
        {
            MinRunsPerPair = 2,
            MaxRunsPerPair = 8,
            FitnessExponent = 1d,
            DiversityBoost = 0d
        };

        Assert.Equal(8u, BasicsReproductionBudgetPlanner.ResolveRunCount(policy, baseRunCount: 1, normalizedFitness: 1f, normalizedNovelty: 1f));
        Assert.Equal(8u, BasicsReproductionBudgetPlanner.ResolveRunCount(policy, baseRunCount: 2, normalizedFitness: 1f, normalizedNovelty: 1f));
        Assert.Equal(8u, BasicsReproductionBudgetPlanner.ResolveRunCount(policy, baseRunCount: 12, normalizedFitness: 1f, normalizedNovelty: 1f));
        Assert.Equal(2u, BasicsReproductionBudgetPlanner.ResolveRunCount(policy, baseRunCount: 8, normalizedFitness: 0f, normalizedNovelty: 0f));
        Assert.Equal(5u, BasicsReproductionBudgetPlanner.ResolveRunCount(policy, baseRunCount: 5, normalizedFitness: 0.5f, normalizedNovelty: 0f));
    }

    [Fact]
    public void ReproductionBudgetPlanner_UsesConstantRunCount_WhenMinEqualsMax()
    {
        var policy = new BasicsRunAllocationPolicy
        {
            MinRunsPerPair = 3,
            MaxRunsPerPair = 3
        };

        Assert.Equal(3u, BasicsReproductionBudgetPlanner.ResolveRunCount(policy, baseRunCount: 0, normalizedFitness: 0f, normalizedNovelty: 0f));
        Assert.Equal(3u, BasicsReproductionBudgetPlanner.ResolveRunCount(policy, baseRunCount: 32, normalizedFitness: 1f, normalizedNovelty: 1f));
    }

    [Fact]
    public void DefaultMetricsContract_IncludesBestBrainComplexityMetrics()
    {
        var metrics = BasicsMetricsContract.Default.RequiredMetrics;

        Assert.Contains(BasicsMetricId.BestCandidateInternalNeuronCount, metrics);
        Assert.Contains(BasicsMetricId.BestCandidateAxonCount, metrics);
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
