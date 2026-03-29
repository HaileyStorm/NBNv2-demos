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

public sealed record BasicsExecutionBestCandidateSummary(
    string ArtifactSha256,
    string SpeciesId,
    float Accuracy,
    float Fitness,
    IReadOnlyDictionary<string, float> ScoreBreakdown,
    IReadOnlyList<string> Diagnostics);

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
