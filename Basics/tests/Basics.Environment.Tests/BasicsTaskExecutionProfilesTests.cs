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
        Assert.Equal(10, profile.VariationBand.MaxAxonDelta);
        Assert.True(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(1, profile.SeedShape.MinActiveInternalRegionCount);
        Assert.Equal(3, profile.SeedShape.MaxInternalNeuronCount);
        Assert.Equal(4, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)4, profile.Sizing.ReproductionRunCount);
        Assert.Equal(2, profile.Sizing.MaxConcurrentBrains);
        Assert.Equal(0.40d, profile.Scheduling.ParentSelection.ExplorationFraction);
        Assert.Equal(0.55d, profile.Scheduling.RunAllocation.DiversityBoost);
    }

    [Fact]
    public void Resolve_ReturnsConservativeBooleanProfile_ForAnd()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("and");

        Assert.Equal(BasicsOutputObservationMode.EventedOutput, profile.OutputObservationMode);
        Assert.Equal(1, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.False(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(1, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)1, profile.Sizing.ReproductionRunCount);
        Assert.Equal(1, profile.Sizing.MaxConcurrentBrains);
        Assert.Equal(0.35d, profile.Scheduling.ParentSelection.DiversityWeight);
        Assert.Equal((uint)12, profile.Scheduling.RunAllocation.MaxRunsPerPair);
    }

    [Fact]
    public void Resolve_ReturnsConservativeBooleanProfile_ForGt()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("gt");

        Assert.Equal(BasicsOutputObservationMode.EventedOutput, profile.OutputObservationMode);
        Assert.Equal(1, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)1, profile.Sizing.ReproductionRunCount);
        Assert.Equal(1, profile.Sizing.MaxConcurrentBrains);
    }

    [Fact]
    public void Resolve_ReturnsRicherVectorProfile_ForMultiplication()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("multiplication");

        Assert.Equal(BasicsOutputObservationMode.VectorBuffer, profile.OutputObservationMode);
        Assert.Equal(3, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.True(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(2, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)2, profile.Sizing.ReproductionRunCount);
        Assert.Equal(1, profile.Sizing.MaxConcurrentBrains);
        Assert.Equal(0.40d, profile.Scheduling.ParentSelection.ExplorationFraction);
        Assert.Equal((uint)4, profile.Scheduling.RunAllocation.MaxRunsPerPair);
    }

    [Fact]
    public void Resolve_FallsBackToBaselineProfile_ForUnknownTask()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("unknown-task");

        Assert.Equal(BasicsOutputObservationMode.VectorPotential, profile.OutputObservationMode);
        Assert.Null(profile.Sizing.InitialPopulationCount);
        Assert.Equal(2, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.True(profile.VariationBand.AllowAxonReroute);
    }
}
