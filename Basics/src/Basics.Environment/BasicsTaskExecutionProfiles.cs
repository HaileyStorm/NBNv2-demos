namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsTaskExecutionProfile(
    BasicsOutputObservationMode OutputObservationMode,
    BasicsOutputSamplingPolicy OutputSamplingPolicy,
    BasicsDiversityPreset DiversityPreset,
    BasicsAdaptiveDiversityOptions AdaptiveDiversity,
    BasicsSeedVariationBand VariationBand,
    BasicsSeedShapeConstraints SeedShape,
    BasicsSizingOverrides Sizing,
    BasicsReproductionSchedulingPolicy Scheduling,
    BasicsExecutionStopCriteria StopCriteria,
    BasicsTaskSettings? TaskSettings = null);

public static class BasicsTaskExecutionProfiles
{
    private const int DefaultPopulationCount = 64;
    private const int DefaultMaxConcurrentBrains = 32;

    private static readonly BasicsTaskExecutionProfile DefaultProfile = new(
        OutputObservationMode: BasicsOutputObservationMode.VectorPotential,
        OutputSamplingPolicy: new BasicsOutputSamplingPolicy(),
        DiversityPreset: BasicsDiversityPreset.Medium,
        AdaptiveDiversity: new BasicsAdaptiveDiversityOptions(),
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
        Sizing: CreateDefaultSizing(),
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
        OutputSamplingPolicy: new BasicsOutputSamplingPolicy
        {
            MaxReadyWindowTicks = 4,
            SampleRepeatCount = 1
        },
        DiversityPreset: BasicsDiversityPreset.Low,
        AdaptiveDiversity: new BasicsAdaptiveDiversityOptions
        {
            Enabled = true,
            StallGenerationWindow = 4
        },
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
            InitialPopulationCount = DefaultPopulationCount,
            MinimumPopulationCount = DefaultPopulationCount,
            MaximumPopulationCount = DefaultPopulationCount,
            ReproductionRunCount = 1,
            MaxConcurrentBrains = DefaultMaxConcurrentBrains
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
        OutputSamplingPolicy = new BasicsOutputSamplingPolicy
        {
            MaxReadyWindowTicks = 4,
            SampleRepeatCount = 1
        },
        DiversityPreset = BasicsDiversityPreset.High,
        AdaptiveDiversity = new BasicsAdaptiveDiversityOptions
        {
            Enabled = true,
            StallGenerationWindow = 6
        },
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
            InitialPopulationCount = DefaultPopulationCount,
            MinimumPopulationCount = DefaultPopulationCount,
            MaximumPopulationCount = DefaultPopulationCount,
            ReproductionRunCount = 4,
            MaxConcurrentBrains = DefaultMaxConcurrentBrains
        },
        Scheduling = BasicsDiversityTuning.CreateScheduling(BasicsDiversityPreset.High)
    };

    private static readonly BasicsTaskExecutionProfile XorProfile = RicherExplorationProfile;

    private static readonly BasicsTaskExecutionProfile MultiplicationProfile = RicherExplorationProfile with
    {
        OutputObservationMode = BasicsOutputObservationMode.EventedOutput,
        OutputSamplingPolicy = new BasicsOutputSamplingPolicy
        {
            MaxReadyWindowTicks = 4,
            SampleRepeatCount = 1
        },
        AdaptiveDiversity = new BasicsAdaptiveDiversityOptions
        {
            Enabled = true,
            StallGenerationWindow = 8
        },
        VariationBand = RicherExplorationProfile.VariationBand with
        {
            MaxAxonDelta = 14
        },
        Sizing = new BasicsSizingOverrides
        {
            InitialPopulationCount = DefaultPopulationCount,
            MinimumPopulationCount = DefaultPopulationCount,
            MaximumPopulationCount = DefaultPopulationCount,
            ReproductionRunCount = 3,
            MaxConcurrentBrains = DefaultMaxConcurrentBrains
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

    private static BasicsSizingOverrides CreateDefaultSizing() => new()
    {
        InitialPopulationCount = DefaultPopulationCount,
        MinimumPopulationCount = DefaultPopulationCount,
        MaximumPopulationCount = DefaultPopulationCount,
        MaxConcurrentBrains = DefaultMaxConcurrentBrains
    };
}
