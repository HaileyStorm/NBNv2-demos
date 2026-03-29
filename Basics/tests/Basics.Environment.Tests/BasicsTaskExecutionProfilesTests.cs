using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsTaskExecutionProfilesTests
{
    [Fact]
    public void Resolve_ReturnsXorProfile_WithSteadyProgressDefaults()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("xor");

        Assert.Equal(BasicsOutputObservationMode.EventedOutput, profile.OutputObservationMode);
        Assert.Equal(3, profile.VariationBand.MaxInternalNeuronDelta);
        Assert.Equal(10, profile.VariationBand.MaxAxonDelta);
        Assert.True(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(1, profile.SeedShape.MinActiveInternalRegionCount);
        Assert.Equal(3, profile.SeedShape.MaxInternalNeuronCount);
        Assert.Equal(2, profile.Sizing.InitialPopulationCount);
        Assert.Equal((uint)4, profile.Sizing.ReproductionRunCount);
        Assert.Equal(1, profile.Sizing.MaxConcurrentBrains);
        Assert.Equal(0.40d, profile.Scheduling.ParentSelection.ExplorationFraction);
        Assert.Equal(0.55d, profile.Scheduling.RunAllocation.DiversityBoost);
    }

    [Fact]
    public void Resolve_FallsBackToBaselineProfile_ForNonXorTasks()
    {
        var profile = BasicsTaskExecutionProfiles.Resolve("and");

        Assert.Equal(BasicsOutputObservationMode.VectorPotential, profile.OutputObservationMode);
        Assert.Null(profile.Sizing.InitialPopulationCount);
        Assert.False(profile.VariationBand.AllowFunctionMutation);
        Assert.Equal(0.35d, profile.Scheduling.ParentSelection.DiversityWeight);
        Assert.Equal((uint)12, profile.Scheduling.RunAllocation.MaxRunsPerPair);
    }
}
