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

        return BasicsContractValidationResult.FromErrors(errors);
    }

    public bool IsSatisfied(float accuracy, float fitness)
        => accuracy >= TargetAccuracy && fitness >= TargetFitness;
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
    float BestAccuracy,
    float BestFitness,
    float MeanFitness,
    ArtifactRef? EffectiveTemplateDefinition,
    BasicsResolvedSeedShape? SeedShape,
    BasicsExecutionBestCandidateSummary? BestCandidate,
    IReadOnlyList<float> AccuracyHistory,
    IReadOnlyList<float> BestFitnessHistory);
