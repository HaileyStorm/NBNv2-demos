using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsTaskExecutionProfilesTests
{
    [Fact]
    public void Resolve_ReturnsXorProfile_WithSteadyProgressDefaults()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("xor");

        Assert.Equal(BasicsOutputObservationMode.VectorPotential, profile.OutputObservationMode);
        Assert.Equal(3, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.Equal(14, profile.VariationBand.MaxAxonDelta);
        Assert.True(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(1, profile.SeedShape.MinActiveInternalRegionCount);
        Assert.Equal(6, profile.SeedShape.MaxInternalNeuronCount);
        Assert.Equal(32, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)4, profile.Sizing.ReproductionRunCount);
        Assert.Equal(32, profile.Sizing.MaxConcurrentBrains);
        Assert.Equal(1, profile.OutputSamplingPolicy.SampleRepeatCount);
        Assert.True(profile.AdaptiveDiversity.Enabled);
        Assert.Equal(6, profile.AdaptiveDiversity.StallGenerationWindow);
        Assert.Equal(0.25d, profile.Scheduling.ParentSelection.EliteFraction);
        Assert.Equal(0.53d, profile.Scheduling.ParentSelection.ExplorationFraction);
        Assert.Equal((uint)8, profile.Scheduling.RunAllocation.MaxRunsPerPair);
        Assert.Equal(0.60d, profile.Scheduling.RunAllocation.DiversityBoost);
        Assert.Null(profile.StopCriteria.MaximumGenerations);
    }

    [Fact]
    public void Resolve_ReturnsConservativeBooleanProfile_ForAnd()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("and");

        Assert.Equal(BasicsOutputObservationMode.EventedOutput, profile.OutputObservationMode);
        Assert.Equal(1, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.False(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(32, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)1, profile.Sizing.ReproductionRunCount);
        Assert.Equal(32, profile.Sizing.MaxConcurrentBrains);
        Assert.Equal(0.35d, profile.Scheduling.ParentSelection.DiversityWeight);
        Assert.Equal((uint)12, profile.Scheduling.RunAllocation.MaxRunsPerPair);
        Assert.NotNull(profile.TaskSettings);
        Assert.Equal(0f, profile.TaskSettings!.BooleanTruthTable.LowInputValue);
        Assert.Equal(1f, profile.TaskSettings.BooleanTruthTable.HighInputValue);
        Assert.True(profile.AdaptiveDiversity.Enabled);
        Assert.Equal(4, profile.AdaptiveDiversity.StallGenerationWindow);
        Assert.Equal(4, profile.OutputSamplingPolicy.MaxReadyWindowTicks);
        Assert.Equal(1, profile.OutputSamplingPolicy.SampleRepeatCount);
    }

    [Fact]
    public void Resolve_ReturnsConservativeBooleanProfile_ForGt()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("gt");

        Assert.Equal(BasicsOutputObservationMode.EventedOutput, profile.OutputObservationMode);
        Assert.Equal(32, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)1, profile.Sizing.ReproductionRunCount);
        Assert.Equal(32, profile.Sizing.MaxConcurrentBrains);
        Assert.NotNull(profile.TaskSettings);
        Assert.Equal(3, profile.TaskSettings!.Gt.UniqueInputValueCount);
        Assert.True(profile.AdaptiveDiversity.Enabled);
        Assert.Equal(4, profile.AdaptiveDiversity.StallGenerationWindow);
        Assert.Equal(4, profile.OutputSamplingPolicy.MaxReadyWindowTicks);
        Assert.Equal(1, profile.OutputSamplingPolicy.SampleRepeatCount);
    }

    [Fact]
    public void Resolve_ReturnsRicherVectorProfile_ForMultiplication()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("multiplication");

        Assert.Equal(BasicsOutputObservationMode.EventedOutput, profile.OutputObservationMode);
        Assert.Equal(3, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.Equal(14, profile.VariationBand.MaxAxonDelta);
        Assert.True(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(32, profile.Sizing.InitialPopulationCount);
        Assert.Equal(32, profile.Sizing.MinimumPopulationCount);
        Assert.Equal(64, profile.Sizing.MaximumPopulationCount);
        Assert.Equal((uint)3, profile.Sizing.ReproductionRunCount);
        Assert.Equal(32, profile.Sizing.MaxConcurrentBrains);
        Assert.Equal(0.43d, profile.Scheduling.ParentSelection.DiversityWeight);
        Assert.Equal(0.53d, profile.Scheduling.ParentSelection.ExplorationFraction);
        Assert.Equal((uint)3, profile.Scheduling.RunAllocation.MinRunsPerPair);
        Assert.Equal((uint)8, profile.Scheduling.RunAllocation.MaxRunsPerPair);
        Assert.Equal(0.60d, profile.Scheduling.RunAllocation.DiversityBoost);
        Assert.Null(profile.StopCriteria.MaximumGenerations);
        Assert.NotNull(profile.TaskSettings);
        Assert.Equal(7, profile.TaskSettings!.Multiplication.UniqueInputValueCount);
        Assert.Equal(0.03f, profile.TaskSettings.Multiplication.AccuracyTolerance);
        Assert.True(profile.AdaptiveDiversity.Enabled);
        Assert.Equal(8, profile.AdaptiveDiversity.StallGenerationWindow);
        Assert.Equal(4, profile.OutputSamplingPolicy.MaxReadyWindowTicks);
        Assert.Equal(1, profile.OutputSamplingPolicy.SampleRepeatCount);
    }

    [Fact]
    public void Resolve_FallsBackToBaselineProfile_ForUnknownTask()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("unknown-task");

        Assert.Equal(BasicsOutputObservationMode.VectorPotential, profile.OutputObservationMode);
        Assert.Null(profile.Sizing.InitialPopulationCount);
        Assert.True(profile.AdaptiveDiversity.Enabled);
        Assert.Equal(4, profile.AdaptiveDiversity.StallGenerationWindow);
        Assert.Equal(2, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.True(profile.VariationBand.AllowAxonReroute);
        Assert.Null(profile.StopCriteria.MaximumGenerations);
    }
}
