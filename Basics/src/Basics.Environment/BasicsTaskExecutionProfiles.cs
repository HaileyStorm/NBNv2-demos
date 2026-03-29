namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsTaskExecutionProfile(
    BasicsOutputObservationMode OutputObservationMode,
    BasicsSeedVariationBand VariationBand,
    BasicsSeedShapeConstraints SeedShape,
    BasicsSizingOverrides Sizing,
    BasicsReproductionSchedulingPolicy Scheduling);

public static class BasicsTaskExecutionProfiles
{
    private static readonly BasicsTaskExecutionProfile DefaultProfile = new(
        OutputObservationMode: BasicsOutputObservationMode.VectorPotential,
        VariationBand: new BasicsSeedVariationBand
        {
            MaxInternalNeuronDelta = 2,
            MaxAxonDelta = 8,
            MaxStrengthCodeDelta = 4,
            MaxParameterCodeDelta = 4,
            AllowFunctionMutation = false,
            AllowAxonReroute = true,
            AllowRegionSetChange = false
        },
        SeedShape: new BasicsSeedShapeConstraints(),
        Sizing: new BasicsSizingOverrides(),
        Scheduling: new BasicsReproductionSchedulingPolicy
        {
            ParentSelection = new BasicsParentSelectionPolicy
            {
                FitnessWeight = 0.55d,
                DiversityWeight = 0.35d,
                SpeciesBalanceWeight = 0.15d,
                EliteFraction = 0.10d,
                ExplorationFraction = 0.25d,
                MaxParentsPerSpecies = 8
            },
            RunAllocation = new BasicsRunAllocationPolicy
            {
                MinRunsPerPair = 2,
                MaxRunsPerPair = 12,
                FitnessExponent = 1.20d,
                DiversityBoost = 0.35d
            }
        });

    private static readonly BasicsTaskExecutionProfile XorProfile = DefaultProfile with
    {
        OutputObservationMode = BasicsOutputObservationMode.EventedOutput,
        VariationBand = new BasicsSeedVariationBand
        {
            MaxInternalNeuronDelta = 3,
            MaxAxonDelta = 10,
            MaxStrengthCodeDelta = 6,
            MaxParameterCodeDelta = 6,
            AllowFunctionMutation = true,
            AllowAxonReroute = true,
            AllowRegionSetChange = false
        },
        SeedShape = new BasicsSeedShapeConstraints
        {
            MinActiveInternalRegionCount = 1,
            MaxActiveInternalRegionCount = 1,
            MinInternalNeuronCount = 2,
            MaxInternalNeuronCount = 3,
            MinAxonCount = 6,
            MaxAxonCount = 10
        },
        Sizing = new BasicsSizingOverrides
        {
            InitialPopulationCount = 2,
            ReproductionRunCount = 4,
            MaxConcurrentBrains = 1
        },
        Scheduling = new BasicsReproductionSchedulingPolicy
        {
            ParentSelection = new BasicsParentSelectionPolicy
            {
                FitnessWeight = 0.50d,
                DiversityWeight = 0.45d,
                SpeciesBalanceWeight = 0.15d,
                EliteFraction = 0.08d,
                ExplorationFraction = 0.40d,
                MaxParentsPerSpecies = 8
            },
            RunAllocation = new BasicsRunAllocationPolicy
            {
                MinRunsPerPair = 2,
                MaxRunsPerPair = 6,
                FitnessExponent = 1.10d,
                DiversityBoost = 0.55d
            }
        }
    };

    public static BasicsTaskExecutionProfile Resolve(string? taskId)
        => string.Equals(taskId, "xor", StringComparison.OrdinalIgnoreCase)
            ? XorProfile
            : DefaultProfile;
}
