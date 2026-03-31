using Nbn.Proto;

using Nbn.Shared;

namespace Nbn.Demos.Basics.Environment;

public enum BasicsExecutionState
{
    Idle = 0,
    Starting = 1,
    Running = 2,
    Succeeded = 3,
    Stopping = 4,
    Stopped = 5,
    Failed = 6
}

public sealed record BasicsExecutionStopCriteria
{
    public float TargetAccuracy { get; init; } = 1f;
    public float TargetFitness { get; init; } = 0.999f;
    public int? MaximumGenerations { get; init; }

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (!float.IsFinite(TargetAccuracy) || TargetAccuracy < 0f || TargetAccuracy > 1f)
        {
            errors.Add("TargetAccuracy must be a finite value between 0 and 1.");
        }

        if (!float.IsFinite(TargetFitness) || TargetFitness < 0f || TargetFitness > 1f)
        {
            errors.Add("TargetFitness must be a finite value between 0 and 1.");
        }

        if (MaximumGenerations is <= 0)
        {
            errors.Add("MaximumGenerations must be > 0 when set.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }

    public bool IsSatisfied(float accuracy, float fitness)
        => accuracy >= TargetAccuracy && fitness >= TargetFitness;

    public bool IsGenerationLimitReached(int generation)
        => MaximumGenerations.HasValue && generation >= MaximumGenerations.Value;
}

public sealed record BasicsTemplatePublishingOptions
{
    public string BindHost { get; init; } = NetworkAddressDefaults.DefaultBindHost;
    public string? AdvertiseHost { get; init; }
    public string? BackingStoreRoot { get; init; }
}

public sealed record BasicsResolvedSeedShape(
    int ActiveInternalRegionCount,
    int InternalNeuronCount,
    int AxonCount);

public sealed record BasicsTemplateBuildResult(
    byte[] Bytes,
    BasicsResolvedSeedShape Shape);

public sealed record BasicsDefinitionComplexitySummary(
    int ActiveInternalRegionCount,
    int InternalNeuronCount,
    int AxonCount);

public sealed record BasicsExecutionBestCandidateSummary(
    ArtifactRef DefinitionArtifact,
    ArtifactRef? SnapshotArtifact,
    Guid? ActiveBrainId,
    string SpeciesId,
    float Accuracy,
    float Fitness,
    BasicsDefinitionComplexitySummary? Complexity,
    IReadOnlyDictionary<string, float> ScoreBreakdown,
    IReadOnlyList<string> Diagnostics)
{
    public string ArtifactSha256 => DefinitionArtifact.ToSha256Hex();

    public bool HasRetainedBrain => ActiveBrainId.HasValue && ActiveBrainId.Value != Guid.Empty;

    public bool HasSnapshotArtifact => SnapshotArtifact is not null && SnapshotArtifact.TryToSha256Bytes(out _);
}

public sealed record BasicsExecutionBatchTimingSummary(
    int Generation,
    int BatchIndex,
    int BatchCount,
    int BrainCount,
    int SuccessfulBrainCount,
    int FailedBrainCount,
    double BatchDurationSeconds,
    double AverageQueueWaitSeconds,
    double AverageSpawnRequestSeconds,
    double AverageSetupSeconds,
    double AverageObservationSeconds,
    string FailureSummary);

public sealed record BasicsExecutionGenerationTimingSummary(
    int Generation,
    int BatchCount,
    int BrainCount,
    int SuccessfulBrainCount,
    int FailedBrainCount,
    double TotalDurationSeconds,
    double AverageBatchDurationSeconds,
    double AverageQueueWaitSeconds,
    double AverageSpawnRequestSeconds,
    double AverageSetupSeconds,
    double AverageObservationSeconds,
    string FailureSummary);

public sealed record BasicsExecutionSnapshot(
    BasicsExecutionState State,
    string StatusText,
    string DetailText,
    ulong? SpeciationEpochId,
    int EvaluationFailureCount,
    string EvaluationFailureSummary,
    int Generation,
    int PopulationCount,
    int ActiveBrainCount,
    int SpeciesCount,
    ulong ReproductionCalls,
    ulong ReproductionRunsObserved,
    float CapacityUtilization,
    float OffspringBestAccuracy,
    float BestAccuracy,
    float OffspringBestFitness,
    float BestFitness,
    float MeanFitness,
    ArtifactRef? EffectiveTemplateDefinition,
    BasicsResolvedSeedShape? SeedShape,
    BasicsExecutionBestCandidateSummary? BestCandidate,
    IReadOnlyList<float> OffspringAccuracyHistory,
    IReadOnlyList<float> AccuracyHistory,
    IReadOnlyList<float> OffspringFitnessHistory,
    IReadOnlyList<float> BestFitnessHistory,
    BasicsExecutionBatchTimingSummary? LatestBatchTiming = null,
    BasicsExecutionGenerationTimingSummary? LatestGenerationTiming = null);
