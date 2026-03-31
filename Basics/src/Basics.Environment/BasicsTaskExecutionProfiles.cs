namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsTaskExecutionProfile(
    BasicsOutputObservationMode OutputObservationMode,
    BasicsDiversityPreset DiversityPreset,
    BasicsSeedVariationBand VariationBand,
    BasicsSeedShapeConstraints SeedShape,
    BasicsSizingOverrides Sizing,
    BasicsReproductionSchedulingPolicy Scheduling,
    BasicsExecutionStopCriteria StopCriteria,
    BasicsTaskSettings? TaskSettings = null);

public static class BasicsTaskExecutionProfiles
{
    private static readonly BasicsTaskExecutionProfile DefaultProfile = new(
        OutputObservationMode: BasicsOutputObservationMode.VectorPotential,
        DiversityPreset: BasicsDiversityPreset.Medium,
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
        },
        StopCriteria: new BasicsExecutionStopCriteria(),
        TaskSettings: new BasicsTaskSettings());

    private static readonly BasicsTaskExecutionProfile ConservativeBooleanProfile = new(
        OutputObservationMode: BasicsOutputObservationMode.EventedOutput,
        DiversityPreset: BasicsDiversityPreset.Low,
        VariationBand: new BasicsSeedVariationBand
        {
            MaxInternalNeuronDelta = 1,
            MaxAxonDelta = 1,
            MaxStrengthCodeDelta = 1,
            MaxParameterCodeDelta = 1,
            AllowFunctionMutation = false,
            AllowAxonReroute = false,
            AllowRegionSetChange = false
        },
        SeedShape: new BasicsSeedShapeConstraints(),
        Sizing: new BasicsSizingOverrides
        {
            InitialPopulationCount = 2,
            ReproductionRunCount = 1,
            MaxConcurrentBrains = 1
        },
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
        },
        StopCriteria: new BasicsExecutionStopCriteria(),
        TaskSettings: new BasicsTaskSettings());

    private static readonly BasicsTaskExecutionProfile RicherExplorationProfile = DefaultProfile with
    {
        OutputObservationMode = BasicsOutputObservationMode.VectorPotential,
        DiversityPreset = BasicsDiversityPreset.High,
        VariationBand = new BasicsSeedVariationBand
        {
            MaxInternalNeuronDelta = 3,
            MaxAxonDelta = 14,
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
            MaxInternalNeuronCount = 6,
            MinAxonCount = 5,
            MaxAxonCount = 33
        },
        Sizing = new BasicsSizingOverrides
        {
            InitialPopulationCount = 4,
            ReproductionRunCount = 4,
            MaxConcurrentBrains = 64
        },
        Scheduling = BasicsDiversityTuning.CreateScheduling(BasicsDiversityPreset.High)
    };

    private static readonly BasicsTaskExecutionProfile XorProfile = RicherExplorationProfile;

    private static readonly BasicsTaskExecutionProfile MultiplicationProfile = RicherExplorationProfile with
    {
        OutputObservationMode = BasicsOutputObservationMode.EventedOutput,
        VariationBand = RicherExplorationProfile.VariationBand with
        {
            MaxAxonDelta = 14
        },
        Sizing = new BasicsSizingOverrides
        {
            InitialPopulationCount = 24,
            MinimumPopulationCount = 12,
            MaximumPopulationCount = 64,
            ReproductionRunCount = 3,
            MaxConcurrentBrains = 64
        }
    };

    public static BasicsTaskExecutionProfile Resolve(string? taskId)
        => taskId?.Trim().ToLowerInvariant() switch
        {
            "and" => ConservativeBooleanProfile,
            "or" => ConservativeBooleanProfile,
            "gt" => ConservativeBooleanProfile,
            "xor" => XorProfile,
            "multiplication" => MultiplicationProfile,
            _ => DefaultProfile
        };
}
