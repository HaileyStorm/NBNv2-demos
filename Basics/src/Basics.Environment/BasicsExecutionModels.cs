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
    public bool RequireBothTargets { get; init; } = true;
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
        => RequireBothTargets
            ? accuracy >= TargetAccuracy && fitness >= TargetFitness
            : accuracy >= TargetAccuracy || fitness >= TargetFitness;

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

public enum BasicsBootstrapOriginKind
{
    UploadedExactCopy = 0,
    UploadedVariation = 1,
    TemplateExactCopy = 2,
    TemplateVariation = 3
}

public sealed record BasicsBootstrapOrigin(
    BasicsBootstrapOriginKind Kind,
    string SourceDisplayName,
    string? SourceContentHash,
    int BootstrapTemplateIndex,
    int? ExactCopyOrdinal)
{
    public bool IsUploadedSeed
        => Kind is BasicsBootstrapOriginKind.UploadedExactCopy or BasicsBootstrapOriginKind.UploadedVariation;

    public bool IsExactCopy
        => Kind is BasicsBootstrapOriginKind.UploadedExactCopy or BasicsBootstrapOriginKind.TemplateExactCopy;
}

public sealed record BasicsExecutionBootstrapCandidateTrace(
    BasicsBootstrapOrigin Origin,
    string ArtifactSha256,
    string SpeciesId,
    float Accuracy,
    float Fitness,
    IReadOnlyDictionary<string, float> ScoreBreakdown,
    IReadOnlyList<string> Diagnostics,
    int Generation);

public sealed record BasicsExecutionBestCandidateSummary(
    ArtifactRef DefinitionArtifact,
    ArtifactRef? SnapshotArtifact,
    Guid? ActiveBrainId,
    string SpeciesId,
    float Accuracy,
    float Fitness,
    BasicsDefinitionComplexitySummary? Complexity,
    IReadOnlyDictionary<string, float> ScoreBreakdown,
    IReadOnlyList<string> Diagnostics,
    int Generation = 0,
    float? AverageReadyTickCount = null,
    float? MinReadyTickCount = null,
    float? MedianReadyTickCount = null,
    float? MaxReadyTickCount = null,
    float? ReadyTickStdDev = null)
{
    public string ArtifactSha256 => DefinitionArtifact.ToSha256Hex();

    public bool HasRetainedBrain => ActiveBrainId.HasValue && ActiveBrainId.Value != Guid.Empty;

    public bool HasSnapshotArtifact => SnapshotArtifact is not null && SnapshotArtifact.TryToSha256Bytes(out _);

    public BasicsBootstrapOrigin? BootstrapOrigin { get; init; }
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
    double AveragePlacementWaitSeconds,
    double AverageSetupSeconds,
    double AverageObservationSeconds,
    double AverageObservationAttemptCount,
    double AverageObservationSecondsPerAttempt,
    double AverageObservationPauseSeconds,
    double AverageObservationResetSeconds,
    double AverageObservationInputSeconds,
    double AverageObservationResumeSeconds,
    double AverageObservationWaitSeconds,
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
    double AveragePlacementWaitSeconds,
    double AverageSetupSeconds,
    double AverageObservationSeconds,
    double AverageObservationAttemptCount,
    double AverageObservationSecondsPerAttempt,
    double AverageObservationPauseSeconds,
    double AverageObservationResetSeconds,
    double AverageObservationInputSeconds,
    double AverageObservationResumeSeconds,
    double AverageObservationWaitSeconds,
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
    IReadOnlyList<float> OffspringBalancedAccuracyHistory,
    IReadOnlyList<float> BalancedAccuracyHistory,
    IReadOnlyList<float> OffspringEdgeAccuracyHistory,
    IReadOnlyList<float> OffspringInteriorAccuracyHistory,
    IReadOnlyList<float> OffspringFitnessHistory,
    IReadOnlyList<float> BestFitnessHistory,
    BasicsExecutionBatchTimingSummary? LatestBatchTiming = null,
    BasicsExecutionGenerationTimingSummary? LatestGenerationTiming = null)
{
    public IReadOnlyList<float> OffspringBehaviorOccupancyHistory { get; init; } = Array.Empty<float>();
    public IReadOnlyList<float> BehaviorOccupancyHistory { get; init; } = Array.Empty<float>();
    public IReadOnlyList<float> OffspringBehaviorPressureHistory { get; init; } = Array.Empty<float>();
    public IReadOnlyList<float> BehaviorPressureHistory { get; init; } = Array.Empty<float>();

    public IReadOnlyList<BasicsExecutionBootstrapCandidateTrace> BootstrapCandidateTraces { get; init; } = Array.Empty<BasicsExecutionBootstrapCandidateTrace>();
}
