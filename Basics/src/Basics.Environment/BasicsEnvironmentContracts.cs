using Nbn.Proto;
using Nbn.Proto.Io;
using Nbn.Shared;
using ProtoControl = Nbn.Proto.Control;
using Repro = Nbn.Proto.Repro;

namespace Nbn.Demos.Basics.Environment;

public static class BasicsIoGeometry
{
    public const uint InputWidth = 2;
    public const uint OutputWidth = 1;

    public static BasicsBrainGeometryValidation Validate(BrainInfo? info)
    {
        if (info is null)
        {
            return new BasicsBrainGeometryValidation(
                IsValid: false,
                ExpectedInputWidth: InputWidth,
                ExpectedOutputWidth: OutputWidth,
                ActualInputWidth: 0,
                ActualOutputWidth: 0,
                FailureReason: "brain_info_missing");
        }

        if (info.InputWidth != InputWidth || info.OutputWidth != OutputWidth)
        {
            return new BasicsBrainGeometryValidation(
                IsValid: false,
                ExpectedInputWidth: InputWidth,
                ExpectedOutputWidth: OutputWidth,
                ActualInputWidth: info.InputWidth,
                ActualOutputWidth: info.OutputWidth,
                FailureReason: $"expected_{InputWidth}x{OutputWidth}_got_{info.InputWidth}x{info.OutputWidth}");
        }

        return new BasicsBrainGeometryValidation(
            IsValid: true,
            ExpectedInputWidth: InputWidth,
            ExpectedOutputWidth: OutputWidth,
            ActualInputWidth: info.InputWidth,
            ActualOutputWidth: info.OutputWidth,
            FailureReason: string.Empty);
    }
}

public sealed record BasicsBrainGeometryValidation(
    bool IsValid,
    uint ExpectedInputWidth,
    uint ExpectedOutputWidth,
    uint ActualInputWidth,
    uint ActualOutputWidth,
    string FailureReason);

public sealed record BasicsContractValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static BasicsContractValidationResult Success { get; } = new(true, Array.Empty<string>());

    public static BasicsContractValidationResult FromErrors(IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var materialized = errors.Where(error => !string.IsNullOrWhiteSpace(error)).Select(error => error.Trim()).ToArray();
        return materialized.Length == 0
            ? Success
            : new BasicsContractValidationResult(false, materialized);
    }
}

public sealed record BasicsSeedVariationBand
{
    public int MaxInternalNeuronDelta { get; init; } = 2;
    public int MaxAxonDelta { get; init; } = 8;
    public int MaxStrengthCodeDelta { get; init; } = 4;
    public int MaxParameterCodeDelta { get; init; } = 4;
    public bool AllowFunctionMutation { get; init; }
    public bool AllowAxonReroute { get; init; } = true;
    public bool AllowRegionSetChange { get; init; }

    public static BasicsSeedVariationBand Minor() => new();

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (MaxInternalNeuronDelta < 0)
        {
            errors.Add("MaxInternalNeuronDelta must be >= 0.");
        }

        if (MaxAxonDelta < 0)
        {
            errors.Add("MaxAxonDelta must be >= 0.");
        }

        if (MaxStrengthCodeDelta < 0)
        {
            errors.Add("MaxStrengthCodeDelta must be >= 0.");
        }

        if (MaxParameterCodeDelta < 0)
        {
            errors.Add("MaxParameterCodeDelta must be >= 0.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public sealed record BasicsSeedShapeConstraints
{
    public int? MinActiveInternalRegionCount { get; init; }
    public int? MaxActiveInternalRegionCount { get; init; }
    public int? MinInternalNeuronCount { get; init; }
    public int? MaxInternalNeuronCount { get; init; }
    public int? MinAxonCount { get; init; }
    public int? MaxAxonCount { get; init; }

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();

        ValidateOptionalMinMax(
            MinActiveInternalRegionCount,
            MaxActiveInternalRegionCount,
            "Active internal region count",
            errors);
        ValidateOptionalMinMax(
            MinInternalNeuronCount,
            MaxInternalNeuronCount,
            "Internal neuron count",
            errors);
        ValidateOptionalMinMax(
            MinAxonCount,
            MaxAxonCount,
            "Axon count",
            errors);

        return BasicsContractValidationResult.FromErrors(errors);
    }

    private static void ValidateOptionalMinMax(int? min, int? max, string label, ICollection<string> errors)
    {
        if (min is < 0)
        {
            errors.Add($"{label} minimum must be >= 0 when set.");
        }

        if (max is < 0)
        {
            errors.Add($"{label} maximum must be >= 0 when set.");
        }

        if (min.HasValue && max.HasValue && max.Value < min.Value)
        {
            errors.Add($"{label} maximum must be >= minimum.");
        }
    }
}

public sealed record BasicsSeedTemplateContract
{
    public required string TemplateId { get; init; }
    public string Description { get; init; } = string.Empty;
    public ArtifactRef? TemplateDefinition { get; init; }
    public BasicsSeedVariationBand InitialVariationBand { get; init; } = BasicsSeedVariationBand.Minor();
    public BasicsSeedShapeConstraints InitialSeedShapeConstraints { get; init; } = new();
    public bool ExpectSingleBootstrapSpecies { get; init; } = true;
    public bool AllowOffTemplateSeeds { get; init; }
    public uint InputWidth { get; init; } = BasicsIoGeometry.InputWidth;
    public uint OutputWidth { get; init; } = BasicsIoGeometry.OutputWidth;

    public static BasicsSeedTemplateContract CreateDefault() =>
        new()
        {
            TemplateId = "basics-template-a",
            Description = "Seed all initial brains from one shared 2->1 template, allowing only bounded minor divergence so reproduction and bootstrap speciation remain coherent."
        };

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(TemplateId))
        {
            errors.Add("TemplateId is required.");
        }

        if (InputWidth != BasicsIoGeometry.InputWidth || OutputWidth != BasicsIoGeometry.OutputWidth)
        {
            errors.Add($"Template geometry must remain {BasicsIoGeometry.InputWidth}->{BasicsIoGeometry.OutputWidth}.");
        }

        if (!ExpectSingleBootstrapSpecies)
        {
            errors.Add("ExpectSingleBootstrapSpecies must stay true for the initial Basics seed family.");
        }

        if (AllowOffTemplateSeeds)
        {
            errors.Add("AllowOffTemplateSeeds must stay false; initial Basics populations are template-anchored.");
        }

        if (TemplateDefinition is not null
            && !string.Equals(TemplateDefinition.MediaType, "application/x-nbn", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("TemplateDefinition must be an application/x-nbn artifact when provided.");
        }

        var variationValidation = InitialVariationBand.Validate();
        if (!variationValidation.IsValid)
        {
            errors.AddRange(variationValidation.Errors);
        }

        var shapeValidation = InitialSeedShapeConstraints.Validate();
        if (!shapeValidation.IsValid)
        {
            errors.AddRange(shapeValidation.Errors);
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public sealed record BasicsSizingOverrides
{
    public int? InitialPopulationCount { get; init; }
    public int? MinimumPopulationCount { get; init; }
    public int? MaximumPopulationCount { get; init; }
    public uint? ReproductionRunCount { get; init; }
    public int? MaxConcurrentBrains { get; init; }

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (InitialPopulationCount is < 2)
        {
            errors.Add("InitialPopulationCount override must be >= 2 when set.");
        }

        if (MinimumPopulationCount is < 2)
        {
            errors.Add("MinimumPopulationCount override must be >= 2 when set.");
        }

        if (MaximumPopulationCount is < 2)
        {
            errors.Add("MaximumPopulationCount override must be >= 2 when set.");
        }

        if (MinimumPopulationCount.HasValue && MaximumPopulationCount.HasValue && MaximumPopulationCount.Value < MinimumPopulationCount.Value)
        {
            errors.Add("MaximumPopulationCount override must be >= MinimumPopulationCount when both are set.");
        }

        if (InitialPopulationCount.HasValue && MinimumPopulationCount.HasValue && InitialPopulationCount.Value < MinimumPopulationCount.Value)
        {
            errors.Add("InitialPopulationCount override must be >= MinimumPopulationCount when both are set.");
        }

        if (InitialPopulationCount.HasValue && MaximumPopulationCount.HasValue && InitialPopulationCount.Value > MaximumPopulationCount.Value)
        {
            errors.Add("InitialPopulationCount override must be <= MaximumPopulationCount when both are set.");
        }

        if (ReproductionRunCount is 0)
        {
            errors.Add("ReproductionRunCount override must be > 0 when set.");
        }

        if (MaxConcurrentBrains is <= 0)
        {
            errors.Add("MaxConcurrentBrains override must be > 0 when set.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public enum BasicsCapacitySource
{
    RuntimePlacementInventory = 0,
    FallbackDefaults = 1
}

public sealed record BasicsCapacityRecommendation(
    BasicsCapacitySource Source,
    int EligibleWorkerCount,
    int RecommendedInitialPopulationCount,
    uint RecommendedReproductionRunCount,
    int RecommendedMaxConcurrentBrains,
    float CapacityScore,
    ulong EffectiveRamFreeBytes,
    string Summary);

public sealed record BasicsInitialBrainSeed(
    string DisplayName,
    byte[] DefinitionBytes,
    bool DuplicateForReproduction,
    BasicsDefinitionComplexitySummary Complexity)
{
    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add("Initial brain DisplayName is required.");
        }

        if (DefinitionBytes.Length == 0)
        {
            errors.Add("Initial brain DefinitionBytes must not be empty.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public enum BasicsMetricId
{
    Accuracy = 0,
    BestFitness = 1,
    MeanFitness = 2,
    PopulationCount = 3,
    ActiveBrainCount = 4,
    SpeciesCount = 5,
    ReproductionCalls = 6,
    ReproductionRunsObserved = 7,
    CapacityUtilization = 8,
    BestAccuracy = 9,
    LatestBatchDuration = 10,
    LatestSetupDuration = 11,
    LatestObservationDuration = 12
}

public sealed record BasicsMetricsContract(IReadOnlyList<BasicsMetricId> RequiredMetrics)
{
    public static BasicsMetricsContract Default { get; } = new(
        new[]
        {
            BasicsMetricId.Accuracy,
            BasicsMetricId.BestAccuracy,
            BasicsMetricId.BestFitness,
            BasicsMetricId.MeanFitness,
            BasicsMetricId.PopulationCount,
            BasicsMetricId.ActiveBrainCount,
            BasicsMetricId.SpeciesCount,
            BasicsMetricId.ReproductionCalls,
            BasicsMetricId.ReproductionRunsObserved,
            BasicsMetricId.CapacityUtilization,
            BasicsMetricId.LatestBatchDuration,
            BasicsMetricId.LatestSetupDuration,
            BasicsMetricId.LatestObservationDuration
        });
}

public sealed record BasicsReproductionPolicy
{
    public Repro.ReproduceConfig Config { get; init; } = CreateDefaultConfig();
    public Repro.StrengthSource StrengthSource { get; init; } = Repro.StrengthSource.StrengthBaseOnly;

    public static BasicsReproductionPolicy CreateDefault() => new();

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (Config is null)
        {
            errors.Add("Reproduction config is required.");
        }
        else if (!Config.HasProtectIoRegionNeuronCounts || !Config.ProtectIoRegionNeuronCounts)
        {
            errors.Add("Basics reproduction defaults must keep protect_io_region_neuron_counts enabled.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }

    private static Repro.ReproduceConfig CreateDefaultConfig()
    {
        var config = ReproductionSettings.CreateDefaultConfig();
        config.ProtectIoRegionNeuronCounts = true;
        return config;
    }
}

public enum BasicsOutputObservationMode
{
    VectorPotential = 0,
    EventedOutput = 1,
    VectorBuffer = 2
}

public static class BasicsOutputObservationModeExtensions
{
    public static bool UsesVectorSubscription(this BasicsOutputObservationMode mode)
        => mode is BasicsOutputObservationMode.VectorPotential or BasicsOutputObservationMode.VectorBuffer;

    public static ProtoControl.OutputVectorSource ResolveVectorSource(this BasicsOutputObservationMode mode)
        => mode == BasicsOutputObservationMode.VectorBuffer
            ? ProtoControl.OutputVectorSource.Buffer
            : ProtoControl.OutputVectorSource.Potential;
}

public sealed record BasicsEnvironmentOptions
{
    public string ClientName { get; init; } = "nbn.basics.environment";
    public BasicsTaskContract SelectedTask { get; init; } = new(
        TaskId: "and",
        DisplayName: "AND",
        InputWidth: BasicsIoGeometry.InputWidth,
        OutputWidth: BasicsIoGeometry.OutputWidth,
        UsesTickAlignedEvaluation: true,
        Description: "Boolean AND over canonical 0/1 inputs and outputs.");
    public BasicsSeedTemplateContract SeedTemplate { get; init; } = BasicsSeedTemplateContract.CreateDefault();
    public BasicsSizingOverrides SizingOverrides { get; init; } = new();
    public IReadOnlyList<BasicsInitialBrainSeed> InitialBrainSeeds { get; init; } = Array.Empty<BasicsInitialBrainSeed>();
    public BasicsMetricsContract Metrics { get; init; } = BasicsMetricsContract.Default;
    public BasicsOutputObservationMode OutputObservationMode { get; init; } = BasicsOutputObservationMode.VectorPotential;
    public BasicsReproductionPolicy Reproduction { get; init; } = BasicsReproductionPolicy.CreateDefault();
    public BasicsReproductionSchedulingPolicy Scheduling { get; init; } = BasicsReproductionSchedulingPolicy.Default;
    public BasicsExecutionStopCriteria StopCriteria { get; init; } = new();

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ClientName))
        {
            errors.Add("ClientName is required.");
        }

        if (string.IsNullOrWhiteSpace(SelectedTask.TaskId) || string.IsNullOrWhiteSpace(SelectedTask.DisplayName))
        {
            errors.Add("SelectedTask must define a task id and display name.");
        }

        if (SelectedTask.InputWidth != BasicsIoGeometry.InputWidth || SelectedTask.OutputWidth != BasicsIoGeometry.OutputWidth)
        {
            errors.Add("SelectedTask must remain bound to the Basics 2->1 geometry.");
        }

        AddValidationErrors(SeedTemplate.Validate(), errors);
        AddValidationErrors(SizingOverrides.Validate(), errors);
        AddValidationErrors(Reproduction.Validate(), errors);
        AddValidationErrors(Scheduling.Validate(), errors);
        AddValidationErrors(StopCriteria.Validate(), errors);
        foreach (var seed in InitialBrainSeeds)
        {
            AddValidationErrors(seed.Validate(), errors);
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }

    private static void AddValidationErrors(BasicsContractValidationResult validation, ICollection<string> errors)
    {
        if (validation.IsValid)
        {
            return;
        }

        foreach (var error in validation.Errors)
        {
            errors.Add(error);
        }
    }
}

public sealed record BasicsEnvironmentPlan(
    BasicsTaskContract SelectedTask,
    BasicsSeedTemplateContract SeedTemplate,
    BasicsSizingOverrides SizingOverrides,
    IReadOnlyList<BasicsInitialBrainSeed> InitialBrainSeeds,
    BasicsCapacityRecommendation Capacity,
    BasicsOutputObservationMode OutputObservationMode,
    BasicsReproductionPolicy Reproduction,
    BasicsReproductionSchedulingPolicy Scheduling,
    BasicsMetricsContract Metrics,
    BasicsExecutionStopCriteria StopCriteria,
    DateTimeOffset PlannedAtUtc);

public sealed record BasicsTaskContract(
    string TaskId,
    string DisplayName,
    uint InputWidth,
    uint OutputWidth,
    bool UsesTickAlignedEvaluation,
    string Description = "");

public sealed record BasicsTaskEvaluationContext(
    uint InputWidth,
    uint OutputWidth,
    bool TickAligned,
    ulong TickBase = 0);

public readonly record struct BasicsTaskSample(
    float InputA,
    float InputB,
    float ExpectedOutput,
    ulong DelayTicks = 0,
    string Label = "");

public readonly record struct BasicsTaskObservation(ulong TickId, float OutputValue);

public sealed record BasicsTaskEvaluationResult(
    float Fitness,
    float Accuracy,
    int SamplesEvaluated,
    int SamplesCorrect,
    IReadOnlyDictionary<string, float> ScoreBreakdown,
    IReadOnlyList<string> Diagnostics);

public interface IBasicsTaskPlugin
{
    BasicsTaskContract Contract { get; }

    IReadOnlyList<BasicsTaskSample> BuildDeterministicDataset();

    BasicsTaskEvaluationResult Evaluate(
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations);
}
