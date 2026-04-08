using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Nbn.Proto;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Runtime.Artifacts;
using Nbn.Shared;
using Repro = Nbn.Proto.Repro;
using ProtoSpec = Nbn.Proto.Speciation;

namespace Nbn.Demos.Basics.Environment;

public sealed class BasicsExecutionSession : IBasicsExecutionRunner
{
    private static readonly TimeSpan VectorObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EventedObservationTimeout = VectorObservationTimeout;
    private static readonly TimeSpan InitialBrainActivationTimeout = TimeSpan.FromSeconds(15);
    // Basics uses an explicit caller-side wait budget so burst placement does not fail on HiveMind's short idle-only default.
    private static readonly TimeSpan DefaultSpawnPlacementTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryableSpawnFailureBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BrainTeardownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BrainTeardownPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultMinimumSpawnRequestInterval = TimeSpan.Zero;
    private static readonly TimeSpan DefaultEvaluationProgressPublishInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultBreedingProgressPublishInterval = TimeSpan.FromMilliseconds(250);
    private const uint ValueOutputIndex = 0;
    private const uint ReadyOutputIndex = 1;
    private const int MaxMemberEvaluationAttempts = 3;
    private const int MinimumPopulationSize = 2;

    private readonly IBasicsRuntimeClient _runtimeClient;
    private readonly BasicsTemplatePublishingOptions _publishingOptions;
    private readonly ReachableArtifactStorePublisher _artifactPublisher = new();
    private readonly Random _random = new(1701);
    private readonly ConcurrentDictionary<Guid, byte> _trackedBrains = new();
    private readonly ConcurrentDictionary<string, BasicsDefinitionComplexitySummary?> _definitionComplexityCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _minimumSpawnRequestInterval;
    private readonly TimeSpan _spawnPlacementTimeout;
    private readonly TimeSpan _evaluationProgressPublishInterval;
    private readonly TimeSpan _breedingProgressPublishInterval;

    private readonly record struct ObservationAttemptResult(BasicsTaskObservation? Observation, string? FailureDetail);

    public BasicsExecutionSession(
        IBasicsRuntimeClient runtimeClient,
        BasicsTemplatePublishingOptions publishingOptions,
        TimeSpan? minimumSpawnRequestInterval = null,
        TimeSpan? spawnPlacementTimeout = null,
        TimeSpan? evaluationProgressPublishInterval = null,
        TimeSpan? breedingProgressPublishInterval = null)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        _publishingOptions = publishingOptions ?? throw new ArgumentNullException(nameof(publishingOptions));
        _minimumSpawnRequestInterval = minimumSpawnRequestInterval ?? DefaultMinimumSpawnRequestInterval;
        _spawnPlacementTimeout = spawnPlacementTimeout ?? DefaultSpawnPlacementTimeout;
        _evaluationProgressPublishInterval = evaluationProgressPublishInterval ?? DefaultEvaluationProgressPublishInterval;
        _breedingProgressPublishInterval = breedingProgressPublishInterval ?? DefaultBreedingProgressPublishInterval;
    }

    public async Task<BasicsExecutionSnapshot> RunAsync(
        BasicsEnvironmentPlan plan,
        IBasicsTaskPlugin taskPlugin,
        Action<BasicsExecutionSnapshot>? onSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(taskPlugin);

        if (!string.Equals(taskPlugin.Contract.TaskId, plan.SelectedTask.TaskId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Execution task mismatch. Plan selected '{plan.SelectedTask.TaskId}' but plugin '{taskPlugin.Contract.TaskId}' was supplied.");
        }

        BasicsResolvedSeedShape? seedShape = null;
        ArtifactRef? effectiveTemplateDefinition = null;
        ulong? speciationEpochId = null;
        var offspringAccuracyHistory = new List<float>();
        var accuracyHistory = new List<float>();
        var offspringBalancedAccuracyHistory = new List<float>();
        var balancedAccuracyHistory = new List<float>();
        var offspringEdgeAccuracyHistory = new List<float>();
        var offspringInteriorAccuracyHistory = new List<float>();
        var offspringFitnessHistory = new List<float>();
        var fitnessHistory = new List<float>();
        ulong reproductionCalls = 0;
        ulong reproductionRunsObserved = 0;
        float bestAccuracySoFar = 0f;
        float bestFitnessSoFar = 0f;
        var stalledGenerationCount = 0;
        BasicsExecutionBestCandidateSummary? bestCandidateSoFar = null;
        List<PopulationMember> population = new();
        BasicsExecutionSnapshot? lastObservedSnapshot = null;
        var publishSnapshot = new Action<BasicsExecutionSnapshot>(snapshot =>
        {
            lastObservedSnapshot = snapshot;
            onSnapshot?.Invoke(snapshot);
        });
        void PublishStartupStatus(string statusText, string detailText)
        {
            PublishSnapshot(
                publishSnapshot,
                BasicsExecutionState.Starting,
                statusText,
                detailText,
                speciationEpochId,
                evaluationFailureCount: 0,
                evaluationFailureSummary: string.Empty,
                generation: 0,
                populationCount: 0,
                activeBrainCount: 0,
                speciesCount: 0,
                reproductionCalls,
                reproductionRunsObserved,
                capacityUtilization: 0f,
                bestAccuracy: 0f,
                bestFitness: 0f,
                meanFitness: 0f,
                effectiveTemplateDefinition,
                seedShape,
                bestCandidate: null,
                accuracyHistory,
                fitnessHistory,
                overallBestAccuracy: bestAccuracySoFar,
                overallBestFitness: bestFitnessSoFar,
                overallBestCandidate: bestCandidateSoFar,
                offspringAccuracyHistory: offspringAccuracyHistory,
                offspringBalancedAccuracyHistory: offspringBalancedAccuracyHistory,
                balancedAccuracyHistory: balancedAccuracyHistory,
                offspringEdgeAccuracyHistory: offspringEdgeAccuracyHistory,
                offspringInteriorAccuracyHistory: offspringInteriorAccuracyHistory,
                offspringFitnessHistory: offspringFitnessHistory);
        }

        PublishStartupStatus(
            "Starting Basics session...",
            $"Preparing template-seeded {plan.SelectedTask.DisplayName} population.");

        try
        {
            var template = await ResolveTemplateDefinitionAsync(plan.SeedTemplate, cancellationToken).ConfigureAwait(false);
            effectiveTemplateDefinition = template.TemplateDefinition;
            seedShape = template.SeedShape;

            PublishStartupStatus(
                "Preparing speciation epoch...",
                "Requesting a fresh speciation epoch before generation 1 seeding begins.");
            speciationEpochId = await EnsureFreshSpeciationEpochAsync(cancellationToken).ConfigureAwait(false);

            var minimumPopulation = Math.Max(MinimumPopulationSize, plan.SizingOverrides.MinimumPopulationCount ?? MinimumPopulationSize);
            var maximumPopulation = Math.Max(
                minimumPopulation,
                plan.SizingOverrides.MaximumPopulationCount
                ?? Math.Max(minimumPopulation, plan.Capacity.RecommendedInitialPopulationCount));
            var targetPopulation = Math.Clamp(
                Math.Max(MinimumPopulationSize, plan.Capacity.RecommendedInitialPopulationCount),
                minimumPopulation,
                maximumPopulation);
            var minimumRequiredInitialPopulation = ResolveMinimumRequiredInitialPopulation(plan.InitialBrainSeeds);
            if (targetPopulation < minimumRequiredInitialPopulation)
            {
                return CreateFinalSnapshot(
                    plan.StopCriteria,
                    publishSnapshot,
                    BasicsExecutionState.Failed,
                    "Execution failed.",
                    $"Initial population {targetPopulation} is below the required minimum {minimumRequiredInitialPopulation} for the uploaded seed brains.",
                    speciationEpochId,
                    0,
                    Array.Empty<PopulationMember>(),
                    0,
                    reproductionCalls,
                    reproductionRunsObserved,
                    effectiveTemplateDefinition,
                    seedShape,
                    accuracyHistory,
                    fitnessHistory,
                    lastObservedSnapshot,
                    bestAccuracySoFar,
                    bestFitnessSoFar,
                    bestCandidateSoFar);
            }

            PublishStartupStatus(
                "Seeding initial population...",
                plan.InitialBrainSeeds.Count == 0
                    ? $"Expanding template family {plan.SeedTemplate.TemplateId} into {targetPopulation} generation-1 brains."
                    : $"Building {targetPopulation} generation-1 brains from {plan.InitialBrainSeeds.Count} uploaded seed brain(s) and bounded variations.");
            population = await SeedInitialPopulationAsync(
                    plan,
                    template.TemplateDefinition,
                    template.Complexity,
                    PublishStartupStatus,
                    targetPopulation,
                    cancellationToken)
                .ConfigureAwait(false);

            if (population.Count == 0)
            {
                return CreateFinalSnapshot(
                    plan.StopCriteria,
                    publishSnapshot,
                    BasicsExecutionState.Failed,
                    "Execution failed.",
                    "The initial template population could not be seeded.",
                    speciationEpochId,
                    0,
                    Array.Empty<PopulationMember>(),
                    0,
                    reproductionCalls,
                    reproductionRunsObserved,
                    effectiveTemplateDefinition,
                    seedShape,
                    accuracyHistory,
                    fitnessHistory,
                    lastObservedSnapshot,
                    bestAccuracySoFar,
                    bestFitnessSoFar,
                    bestCandidateSoFar);
            }

            var generation = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                generation++;
                var evaluationResult = await EvaluatePopulationAsync(
                        plan,
                        taskPlugin,
                        generation,
                        population,
                        plan.OutputObservationMode,
                        plan.OutputSamplingPolicy,
                        speciationEpochId,
                        effectiveTemplateDefinition,
                        seedShape,
                        reproductionCalls,
                        reproductionRunsObserved,
                        offspringAccuracyHistory,
                        accuracyHistory,
                        offspringBalancedAccuracyHistory,
                        balancedAccuracyHistory,
                        offspringEdgeAccuracyHistory,
                        offspringInteriorAccuracyHistory,
                        offspringFitnessHistory,
                        fitnessHistory,
                        bestAccuracySoFar,
                        bestFitnessSoFar,
                        bestCandidateSoFar,
                        publishSnapshot,
                        cancellationToken)
                    .ConfigureAwait(false);
                population = evaluationResult.Population.ToList();

                var generationMetrics = BuildGenerationMetrics(population, plan.StopCriteria, includeWinnerRuntimeState: true, currentGeneration: generation);
                if (IsGenerationFullyFailed(population))
                {
                    (population, bestCandidateSoFar) = await TryRetainBestCandidateForExportAsync(
                            taskPlugin.Contract,
                            population,
                            currentGenerationBest: generationMetrics.BestCandidate,
                            bestCandidateSoFar,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    return CreateFinalSnapshot(
                        plan.StopCriteria,
                        publishSnapshot,
                        BasicsExecutionState.Failed,
                        "Execution failed.",
                        string.IsNullOrWhiteSpace(generationMetrics.EvaluationFailureSummary)
                            ? $"Generation {generation} produced no viable evaluations."
                            : $"Generation {generation} produced no viable evaluations. {generationMetrics.EvaluationFailureSummary}.",
                        speciationEpochId,
                        generation,
                        population,
                        population.Count(member => member.ActiveBrainId != Guid.Empty),
                        reproductionCalls,
                        reproductionRunsObserved,
                        effectiveTemplateDefinition,
                        seedShape,
                        accuracyHistory,
                        fitnessHistory,
                        lastObservedSnapshot,
                        bestAccuracySoFar,
                        bestFitnessSoFar,
                        bestCandidateSoFar,
                        evaluationResult.LatestBatchTiming,
                        evaluationResult.GenerationTiming,
                        offspringAccuracyHistory,
                        offspringBalancedAccuracyHistory,
                        balancedAccuracyHistory,
                        offspringEdgeAccuracyHistory,
                        offspringInteriorAccuracyHistory,
                        offspringFitnessHistory);
                }

                var previousBestCandidate = bestCandidateSoFar;
                var generationImproved = DidGenerationImprove(generationMetrics, bestAccuracySoFar, bestFitnessSoFar, bestCandidateSoFar);
                UpdateBestSoFar(generationMetrics, ref bestAccuracySoFar, ref bestFitnessSoFar, ref bestCandidateSoFar);
                bestCandidateSoFar = await EnsureBestCandidateSnapshotAsync(previousBestCandidate, bestCandidateSoFar, cancellationToken).ConfigureAwait(false);
                stalledGenerationCount = generationImproved ? 0 : stalledGenerationCount + 1;
                offspringAccuracyHistory.Add(generationMetrics.OffspringBestAccuracy);
                accuracyHistory.Add(generationMetrics.BestAccuracy);
                if (string.Equals(taskPlugin.Contract.TaskId, "multiplication", StringComparison.OrdinalIgnoreCase))
                {
                    offspringBalancedAccuracyHistory.Add(generationMetrics.OffspringBestBalancedAccuracy);
                    balancedAccuracyHistory.Add(generationMetrics.BestBalancedAccuracy);
                    offspringEdgeAccuracyHistory.Add(generationMetrics.OffspringBestEdgeAccuracy);
                    offspringInteriorAccuracyHistory.Add(generationMetrics.OffspringBestInteriorAccuracy);
                }
                offspringFitnessHistory.Add(generationMetrics.OffspringBestFitness);
                fitnessHistory.Add(generationMetrics.BestFitness);

                var generationSummary = CreateSnapshot(
                    plan.StopCriteria,
                    BasicsExecutionState.Running,
                    $"Generation {generation} evaluated.",
                    BuildGenerationDetail(generation, generationMetrics, evaluationResult.GenerationTiming),
                    speciationEpochId,
                    generation,
                    population,
                    activeBrainCount: 0,
                    reproductionCalls,
                    reproductionRunsObserved,
                    effectiveTemplateDefinition,
                    seedShape,
                    accuracyHistory,
                    fitnessHistory,
                    offspringAccuracyHistory: offspringAccuracyHistory,
                    offspringBalancedAccuracyHistory: offspringBalancedAccuracyHistory,
                    balancedAccuracyHistory: balancedAccuracyHistory,
                    offspringEdgeAccuracyHistory: offspringEdgeAccuracyHistory,
                    offspringInteriorAccuracyHistory: offspringInteriorAccuracyHistory,
                    offspringFitnessHistory: offspringFitnessHistory,
                    overallBestAccuracy: bestAccuracySoFar,
                    overallBestFitness: bestFitnessSoFar,
                    overallBestCandidate: bestCandidateSoFar,
                    latestBatchTiming: evaluationResult.LatestBatchTiming,
                    latestGenerationTiming: evaluationResult.GenerationTiming);
                publishSnapshot(generationSummary);

                if (plan.StopCriteria.IsSatisfied(generationMetrics.BestAccuracy, generationMetrics.BestFitness))
                {
                    (population, bestCandidateSoFar) = await TryRetainBestCandidateForExportAsync(
                            taskPlugin.Contract,
                            population,
                            currentGenerationBest: generationMetrics.BestCandidate,
                            bestCandidateSoFar,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    var retainedWinnerCount = population.Count(member => member.ActiveBrainId != Guid.Empty);
                    return CreateFinalSnapshot(
                        plan.StopCriteria,
                        publishSnapshot,
                        BasicsExecutionState.Succeeded,
                        "Execution reached the configured stop target.",
                        BuildStopTargetDetail(generation, taskPlugin.Contract.DisplayName, plan.StopCriteria, generationMetrics.BestCandidate),
                        speciationEpochId,
                        generation,
                        population,
                        retainedWinnerCount,
                        reproductionCalls,
                        reproductionRunsObserved,
                        effectiveTemplateDefinition,
                        seedShape,
                        accuracyHistory,
                        fitnessHistory,
                        lastObservedSnapshot,
                        bestAccuracySoFar,
                        bestFitnessSoFar,
                        bestCandidateSoFar,
                        evaluationResult.LatestBatchTiming,
                        evaluationResult.GenerationTiming,
                        offspringAccuracyHistory,
                        offspringBalancedAccuracyHistory,
                        balancedAccuracyHistory,
                        offspringEdgeAccuracyHistory,
                        offspringInteriorAccuracyHistory,
                        offspringFitnessHistory);
                }

                if (plan.StopCriteria.IsGenerationLimitReached(generation))
                {
                    (population, bestCandidateSoFar) = await TryRetainBestCandidateForExportAsync(
                            taskPlugin.Contract,
                            population,
                            currentGenerationBest: generationMetrics.BestCandidate,
                            bestCandidateSoFar,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    var retainedBestCount = population.Count(member => member.ActiveBrainId != Guid.Empty);
                    return CreateFinalSnapshot(
                        plan.StopCriteria,
                        publishSnapshot,
                        BasicsExecutionState.Stopped,
                        "Execution reached the configured generation limit.",
                        BuildGenerationLimitDetail(generation, plan.StopCriteria, bestCandidateSoFar),
                        speciationEpochId,
                        generation,
                        population,
                        retainedBestCount,
                        reproductionCalls,
                        reproductionRunsObserved,
                        effectiveTemplateDefinition,
                        seedShape,
                        accuracyHistory,
                        fitnessHistory,
                        lastObservedSnapshot,
                        bestAccuracySoFar,
                        bestFitnessSoFar,
                        bestCandidateSoFar,
                        evaluationResult.LatestBatchTiming,
                        evaluationResult.GenerationTiming,
                        offspringAccuracyHistory,
                        offspringBalancedAccuracyHistory,
                        balancedAccuracyHistory,
                        offspringEdgeAccuracyHistory,
                        offspringInteriorAccuracyHistory,
                        offspringFitnessHistory);
                }

                population = await TeardownPopulationBrainsAsync(population, CancellationToken.None).ConfigureAwait(false);
                var breedingProgressStopwatch = Stopwatch.StartNew();
                var breedingProgressPublished = false;

                var nextGeneration = await BreedNextGenerationAsync(
                        plan,
                        population,
                        targetPopulation,
                        minimumPopulation,
                        stalledGenerationCount,
                        cancellationToken,
                        onBreedProgress: (detail, activePopulationCount) =>
                        {
                            var forcePublish = !breedingProgressPublished
                                || detail.Contains("Skipped", StringComparison.Ordinal);
                            if (!forcePublish
                                && breedingProgressPublished
                                && _breedingProgressPublishInterval > TimeSpan.Zero
                                && breedingProgressStopwatch.Elapsed < _breedingProgressPublishInterval)
                            {
                                return;
                            }

                            breedingProgressStopwatch.Restart();
                            breedingProgressPublished = true;
                            publishSnapshot(CreateSnapshot(
                                plan.StopCriteria,
                                BasicsExecutionState.Running,
                                $"Breeding generation {generation + 1}...",
                                detail,
                                speciationEpochId,
                                generation,
                                population,
                                activePopulationCount,
                                reproductionCalls,
                                reproductionRunsObserved,
                                effectiveTemplateDefinition,
                                seedShape,
                                accuracyHistory,
                                fitnessHistory,
                                offspringAccuracyHistory: offspringAccuracyHistory,
                                offspringBalancedAccuracyHistory: offspringBalancedAccuracyHistory,
                                balancedAccuracyHistory: balancedAccuracyHistory,
                                offspringEdgeAccuracyHistory: offspringEdgeAccuracyHistory,
                                offspringInteriorAccuracyHistory: offspringInteriorAccuracyHistory,
                                offspringFitnessHistory: offspringFitnessHistory,
                                overallBestAccuracy: bestAccuracySoFar,
                                overallBestFitness: bestFitnessSoFar,
                                overallBestCandidate: bestCandidateSoFar));
                        },
                        onReproductionObserved: runs =>
                        {
                            reproductionCalls++;
                            reproductionRunsObserved += runs;
                        })
                    .ConfigureAwait(false);

                if (nextGeneration.Count == 0)
                {
                    return CreateFinalSnapshot(
                        plan.StopCriteria,
                        publishSnapshot,
                        BasicsExecutionState.Failed,
                        "Execution failed.",
                        $"Generation {generation} could not produce a successor population.",
                        speciationEpochId,
                        generation,
                        population,
                        0,
                        reproductionCalls,
                        reproductionRunsObserved,
                        effectiveTemplateDefinition,
                        seedShape,
                        accuracyHistory,
                        fitnessHistory,
                        lastObservedSnapshot,
                        bestAccuracySoFar,
                        bestFitnessSoFar,
                        bestCandidateSoFar,
                        evaluationResult.LatestBatchTiming,
                        evaluationResult.GenerationTiming,
                        offspringAccuracyHistory,
                        offspringBalancedAccuracyHistory,
                        balancedAccuracyHistory,
                        offspringEdgeAccuracyHistory,
                        offspringInteriorAccuracyHistory,
                        offspringFitnessHistory);
                }

                population = nextGeneration;
            }

            (population, bestCandidateSoFar) = await TryRetainBestCandidateForExportAsync(
                    taskPlugin.Contract,
                    population,
                    currentGenerationBest: null,
                    bestCandidateSoFar,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return CreateFinalSnapshot(
                plan.StopCriteria,
                publishSnapshot,
                BasicsExecutionState.Stopped,
                "Execution stopped.",
                "The run was canceled by the operator.",
                speciationEpochId,
                accuracyHistory.Count,
                population,
                population.Count(member => member.ActiveBrainId != Guid.Empty),
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory,
                lastObservedSnapshot,
                bestAccuracySoFar,
                bestFitnessSoFar,
                bestCandidateSoFar,
                null,
                null,
                offspringAccuracyHistory,
                offspringBalancedAccuracyHistory,
                balancedAccuracyHistory,
                offspringEdgeAccuracyHistory,
                offspringInteriorAccuracyHistory,
                offspringFitnessHistory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            (population, bestCandidateSoFar) = await TryRetainBestCandidateForExportAsync(
                    taskPlugin.Contract,
                    population,
                    currentGenerationBest: null,
                    bestCandidateSoFar,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return CreateFinalSnapshot(
                plan.StopCriteria,
                publishSnapshot,
                BasicsExecutionState.Stopped,
                "Execution stopped.",
                "The run was canceled by the operator.",
                speciationEpochId,
                accuracyHistory.Count,
                population,
                population.Count(member => member.ActiveBrainId != Guid.Empty),
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory,
                lastObservedSnapshot,
                bestAccuracySoFar,
                bestFitnessSoFar,
                bestCandidateSoFar,
                null,
                null,
                offspringAccuracyHistory,
                offspringBalancedAccuracyHistory,
                balancedAccuracyHistory,
                offspringEdgeAccuracyHistory,
                offspringInteriorAccuracyHistory,
                offspringFitnessHistory);
        }
        catch (Exception ex)
        {
            (population, bestCandidateSoFar) = await TryRetainBestCandidateForExportAsync(
                    taskPlugin.Contract,
                    population,
                    currentGenerationBest: null,
                    bestCandidateSoFar,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return CreateFinalSnapshot(
                plan.StopCriteria,
                publishSnapshot,
                BasicsExecutionState.Failed,
                "Execution failed.",
                ex.GetBaseException().Message,
                speciationEpochId,
                accuracyHistory.Count,
                population,
                population.Count(member => member.ActiveBrainId != Guid.Empty),
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory,
                lastObservedSnapshot,
                bestAccuracySoFar,
                bestFitnessSoFar,
                bestCandidateSoFar,
                null,
                null,
                offspringAccuracyHistory,
                offspringBalancedAccuracyHistory,
                balancedAccuracyHistory,
                offspringEdgeAccuracyHistory,
                offspringInteriorAccuracyHistory,
                offspringFitnessHistory);
        }
        finally
        {
            await CleanupTrackedBrainsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _artifactPublisher.DisposeAsync();

    private async Task<ulong> EnsureFreshSpeciationEpochAsync(CancellationToken cancellationToken)
    {
        var current = await _runtimeClient.GetSpeciationConfigAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("Speciation config request returned no response.");
        }

        if (current.FailureReason != ProtoSpec.SpeciationFailureReason.SpeciationFailureNone)
        {
            throw new InvalidOperationException(
                $"Speciation config request failed: {current.FailureReason} {current.FailureDetail}".Trim());
        }

        var config = current.Config?.Clone() ?? new ProtoSpec.SpeciationRuntimeConfig
        {
            PolicyVersion = "default",
            ConfigSnapshotJson = "{}",
            DefaultSpeciesId = "species.default",
            DefaultSpeciesDisplayName = "Default species",
            StartupReconcileDecisionReason = "startup_reconcile"
        };
        var updated = await _runtimeClient.SetSpeciationConfigAsync(config, startNewEpoch: true, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            throw new InvalidOperationException("Speciation epoch start returned no response.");
        }

        if (updated.FailureReason != ProtoSpec.SpeciationFailureReason.SpeciationFailureNone)
        {
            throw new InvalidOperationException(
                $"Speciation epoch start failed: {updated.FailureReason} {updated.FailureDetail}".Trim());
        }

        var previousEpochId = current.CurrentEpoch?.EpochId ?? 0UL;
        var currentEpochId = updated.CurrentEpoch?.EpochId ?? 0UL;
        if (currentEpochId == 0UL || currentEpochId <= previousEpochId)
        {
            throw new InvalidOperationException(
                $"Speciation epoch did not advance (previous={previousEpochId}, current={currentEpochId}).");
        }

        return currentEpochId;
    }

    private async Task ConfigureBrainOutputObservationModeAsync(
        Guid brainId,
        BasicsOutputObservationMode mode,
        BrainInfo? currentInfo,
        CancellationToken cancellationToken)
    {
        if (brainId == Guid.Empty || !mode.UsesVectorSubscription())
        {
            return;
        }

        if (currentInfo is not null && currentInfo.OutputVectorSource == mode.ResolveVectorSource())
        {
            return;
        }

        var ack = await _runtimeClient.SetOutputVectorSourceAsync(
                mode.ResolveVectorSource(),
                brainId,
                cancellationToken)
            .ConfigureAwait(false);
        if (ack is null)
        {
            throw new InvalidOperationException("Output vector source update returned no response.");
        }

        if (!ack.Success)
        {
            throw new InvalidOperationException(
                $"Output vector source update failed: {ack.FailureReasonCode} {ack.FailureMessage}".Trim());
        }

        if (ack.BrainId is not null
            && ack.BrainId.TryToGuid(out var acknowledgedBrainId)
            && acknowledgedBrainId != brainId)
        {
            throw new InvalidOperationException(
                $"Output vector source update targeted brain {brainId} but acknowledged {acknowledgedBrainId}.");
        }
    }

    private async Task ConfigureBrainEvaluationRuntimeStateAsync(
        Guid brainId,
        CancellationToken cancellationToken)
    {
        if (brainId == Guid.Empty)
        {
            return;
        }

        var costAck = await _runtimeClient.SetCostEnergyEnabledAsync(brainId, enabled: false, cancellationToken).ConfigureAwait(false);
        ValidateIoCommandAck(costAck, brainId, "set_cost_energy");

        var plasticityAck = await _runtimeClient.SetPlasticityEnabledAsync(brainId, enabled: false, cancellationToken).ConfigureAwait(false);
        ValidateIoCommandAck(plasticityAck, brainId, "set_plasticity");

        var homeostasisAck = await _runtimeClient.SetHomeostasisEnabledAsync(brainId, enabled: false, cancellationToken).ConfigureAwait(false);
        ValidateIoCommandAck(homeostasisAck, brainId, "set_homeostasis");
    }

    private async Task SynchronizeBrainEvaluationRuntimeConfigAsync(
        Guid brainId,
        CancellationToken cancellationToken)
    {
        if (brainId == Guid.Empty)
        {
            return;
        }

        var syncAck = await _runtimeClient.SynchronizeBrainRuntimeConfigAsync(brainId, cancellationToken).ConfigureAwait(false);
        ValidateIoCommandAck(syncAck, brainId, "sync_brain_runtime_config");
    }

    private static void ValidateIoCommandAck(
        IoCommandAck? ack,
        Guid brainId,
        string command)
    {
        if (ack is null)
        {
            throw new InvalidOperationException($"{command} returned no response for brain {brainId}.");
        }

        if (!ack.Success)
        {
            throw new InvalidOperationException(
                $"{command} failed for brain {brainId}: {ack.Message}".Trim());
        }

        if (ack.BrainId is not null
            && ack.BrainId.TryToGuid(out var acknowledgedBrainId)
            && acknowledgedBrainId != brainId)
        {
            throw new InvalidOperationException(
                $"{command} targeted brain {brainId} but acknowledged {acknowledgedBrainId}.");
        }
    }

    private async Task ResetBrainRuntimeStateForSampleAsync(
        Guid brainId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            var resetAck = await _runtimeClient.ResetBrainRuntimeStateAsync(
                    brainId,
                    resetBuffer: true,
                    resetAccumulator: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (resetAck is not null
                && resetAck.Success
                && (resetAck.BrainId is null
                    || !resetAck.BrainId.TryToGuid(out var acknowledgedBrainId)
                    || acknowledgedBrainId == brainId))
            {
                return;
            }

            if (!IsRetryableTickPhaseInProgressReset(resetAck, brainId))
            {
                ValidateIoCommandAck(resetAck, brainId, "reset_brain_runtime_state");
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"reset_brain_runtime_state failed for brain {brainId}: tick_phase_in_progress persisted beyond retry budget.");
    }

    private static bool IsRetryableTickPhaseInProgressReset(IoCommandAck? ack, Guid brainId)
    {
        if (ack is null || ack.Success)
        {
            return false;
        }

        if (ack.BrainId is not null
            && ack.BrainId.TryToGuid(out var acknowledgedBrainId)
            && acknowledgedBrainId != brainId)
        {
            return false;
        }

        return string.Equals(ack.Message?.Trim(), "tick_phase_in_progress", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(ArtifactRef TemplateDefinition, BasicsResolvedSeedShape? SeedShape, BasicsDefinitionComplexitySummary? Complexity)> ResolveTemplateDefinitionAsync(
        BasicsSeedTemplateContract template,
        CancellationToken cancellationToken)
    {
        if (template.TemplateDefinition is not null)
        {
            var templateDefinition = template.TemplateDefinition.Clone();
            var complexity = await ResolveDefinitionComplexityAsync(templateDefinition, knownShape: null, cancellationToken).ConfigureAwait(false);
            return (templateDefinition, null, complexity);
        }

        var build = BasicsTemplateArtifactBuilder.Build(template);
        var publication = await _artifactPublisher.PublishAsync(
                build.Bytes,
                mediaType: "application/x-nbn",
                backingStoreRoot: ResolveBackingStoreRoot(template.TemplateId),
                bindHost: _publishingOptions.BindHost,
                advertisedHost: _publishingOptions.AdvertiseHost,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (
            publication.ArtifactRef.Clone(),
            build.Shape,
            BuildComplexityFromSeedShape(build.Shape));
    }

    private async Task<List<PopulationMember>> SeedInitialPopulationAsync(
        BasicsEnvironmentPlan plan,
        ArtifactRef templateDefinition,
        BasicsDefinitionComplexitySummary? templateComplexity,
        Action<string, string>? onProgress,
        int targetPopulation,
        CancellationToken cancellationToken)
    {
        var templates = await ResolveInitialSeedTemplatesAsync(
                plan,
                templateDefinition,
                templateComplexity,
                onProgress,
                cancellationToken)
            .ConfigureAwait(false);
        var population = new List<PopulationMember>(targetPopulation);
        var exactCopyOrdinals = templates.ToDictionary(
            static template => template.BootstrapTemplateIndex,
            _ => 0);
        onProgress?.Invoke(
            "Seeding initial population...",
            $"Resolved {templates.Count} bootstrap template(s). Seeding exact copies toward {targetPopulation} generation-1 brains.");
        foreach (var template in templates)
        {
            await CommitArtifactMembershipAsync(
                    template.Definition,
                    Array.Empty<ProtoSpec.SpeciationParentRef>(),
                    explicitSpeciesId: template.SpeciesId,
                    explicitSpeciesDisplayName: template.SpeciesDisplayName,
                    decisionReason: "basics_seed_template",
                    cancellationToken)
                .ConfigureAwait(false);
            for (var copyIndex = 0; copyIndex < template.ExactCopies && population.Count < targetPopulation; copyIndex++)
            {
                exactCopyOrdinals[template.BootstrapTemplateIndex]++;
                population.Add(CreateExactBootstrapPopulationMember(
                    template,
                    exactCopyOrdinals[template.BootstrapTemplateIndex]));
            }
        }

        onProgress?.Invoke(
            "Seeding initial population...",
            $"Seeded {population.Count} of {targetPopulation} generation-1 brains from exact bootstrap copies.");
        var remaining = Math.Max(0, targetPopulation - population.Count);
        var distribution = DistributeAcrossTemplates(remaining, templates.Count);
        for (var templateIndex = 0; templateIndex < templates.Count; templateIndex++)
        {
            if (distribution[templateIndex] <= 0)
            {
                continue;
            }

            onProgress?.Invoke(
                "Generating seed variations...",
                $"Generating up to {distribution[templateIndex]} varied child seed(s) from bootstrap template {templateIndex + 1} of {templates.Count} ({population.Count}/{targetPopulation} ready so far).");
            var children = await GenerateInitialSeedChildrenAsync(
                    plan,
                    templates[templateIndex],
                    templateIndex + 1,
                    templates.Count,
                    distribution[templateIndex],
                    onProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            population.AddRange(children);
            onProgress?.Invoke(
                "Generating seed variations...",
                $"Prepared {population.Count} of {targetPopulation} generation-1 brains after bootstrap template {templateIndex + 1} of {templates.Count}.");
        }

        var refillIndex = 0;
        if (population.Count < targetPopulation && templates.Count > 0)
        {
            onProgress?.Invoke(
                "Filling remaining seed slots...",
                $"Variation generation left {targetPopulation - population.Count} slot(s) short, so Basics is reusing exact bootstrap copies to complete generation 1.");
        }

        while (population.Count < targetPopulation && templates.Count > 0)
        {
            var template = templates[refillIndex % templates.Count];
            exactCopyOrdinals[template.BootstrapTemplateIndex]++;
            population.Add(CreateExactBootstrapPopulationMember(
                template,
                exactCopyOrdinals[template.BootstrapTemplateIndex]));
            refillIndex++;
        }

        onProgress?.Invoke(
            "Initial population ready.",
            $"Prepared {population.Count} generation-1 brains across {population.Select(static member => member.SpeciesId).Distinct(StringComparer.Ordinal).Count()} bootstrap species.");
        return population;
    }

    private async Task<List<ResolvedInitialSeedTemplate>> ResolveInitialSeedTemplatesAsync(
        BasicsEnvironmentPlan plan,
        ArtifactRef defaultTemplateDefinition,
        BasicsDefinitionComplexitySummary? defaultTemplateComplexity,
        Action<string, string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (plan.InitialBrainSeeds.Count == 0)
        {
            var bootstrapSpeciesId = BuildBootstrapSpeciesId(plan.SeedTemplate.TemplateId);
            var bootstrapSpeciesDisplayName = $"{plan.SeedTemplate.TemplateId} bootstrap";
            var resolvedTemplateComplexity = defaultTemplateComplexity
                                             ?? await ResolveDefinitionComplexityAsync(defaultTemplateDefinition, knownShape: null, cancellationToken).ConfigureAwait(false);
            onProgress?.Invoke(
                "Resolving seed templates...",
                $"Using template family {plan.SeedTemplate.TemplateId} as the bootstrap template for generation 1.");
            return
            [
                new ResolvedInitialSeedTemplate(
                    defaultTemplateDefinition.Clone(),
                    null,
                    bootstrapSpeciesId,
                    bootstrapSpeciesDisplayName,
                    resolvedTemplateComplexity,
                    ExactCopies: 1)
                {
                    BootstrapTemplateIndex = 1,
                    SourceDisplayName = plan.SeedTemplate.TemplateId,
                    ExactCopyOriginKind = BasicsBootstrapOriginKind.TemplateExactCopy,
                    VariationOriginKind = BasicsBootstrapOriginKind.TemplateVariation
                }
            ];
        }

        var dedupedSeeds = plan.InitialBrainSeeds
            .GroupBy(ResolveSeedContentHash, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return first with
                {
                    DuplicateForReproduction = group.Any(static seed => seed.DuplicateForReproduction)
                };
            })
            .ToArray();
        var templates = new List<ResolvedInitialSeedTemplate>(dedupedSeeds.Length);
        onProgress?.Invoke(
            "Resolving seed templates...",
            $"Publishing {dedupedSeeds.Length} uploaded initial brain(s) as bootstrap templates.");
        for (var seedIndex = 0; seedIndex < dedupedSeeds.Length; seedIndex++)
        {
            var seed = dedupedSeeds[seedIndex];
            var publication = await _artifactPublisher.PublishAsync(
                    seed.DefinitionBytes,
                    mediaType: "application/x-nbn",
                    backingStoreRoot: ResolveBackingStoreRoot($"{plan.SeedTemplate.TemplateId}-{SanitizePathSegment(seed.DisplayName)}"),
                    bindHost: _publishingOptions.BindHost,
                    advertisedHost: _publishingOptions.AdvertiseHost,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ArtifactRef? snapshotArtifact = null;
            if (seed.SnapshotBytes is { Length: > 0 })
            {
                var snapshotPublication = await _artifactPublisher.PublishAsync(
                        seed.SnapshotBytes,
                        mediaType: "application/x-nbs",
                        backingStoreRoot: ResolveBackingStoreRoot($"{plan.SeedTemplate.TemplateId}-{SanitizePathSegment(seed.DisplayName)}-snapshot"),
                        bindHost: _publishingOptions.BindHost,
                        advertisedHost: _publishingOptions.AdvertiseHost,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                snapshotArtifact = snapshotPublication.ArtifactRef.Clone();
            }
            var speciesId = BuildBootstrapSpeciesId($"{plan.SeedTemplate.TemplateId}-{ShortSeedHash(seed.DefinitionBytes)}");
            templates.Add(new ResolvedInitialSeedTemplate(
                publication.ArtifactRef.Clone(),
                snapshotArtifact,
                speciesId,
                $"{seed.DisplayName} bootstrap",
                seed.Complexity,
                ExactCopies: seed.DuplicateForReproduction ? 2 : 1)
            {
                BootstrapTemplateIndex = seedIndex + 1,
                SourceDisplayName = seed.DisplayName,
                SourceContentHash = ResolveSeedContentHash(seed),
                ExactCopyOriginKind = BasicsBootstrapOriginKind.UploadedExactCopy,
                VariationOriginKind = BasicsBootstrapOriginKind.UploadedVariation
            });
            onProgress?.Invoke(
                "Resolving seed templates...",
                $"Published bootstrap template {templates.Count} of {dedupedSeeds.Length}: {seed.DisplayName}.");
        }

        return templates;
    }

    private async Task<List<PopulationMember>> GenerateInitialSeedChildrenAsync(
        BasicsEnvironmentPlan plan,
        ResolvedInitialSeedTemplate template,
        int templateIndex,
        int templateCount,
        int targetChildCount,
        Action<string, string>? onProgress,
        CancellationToken cancellationToken)
    {
        var seedingConfig = plan.Reproduction.Config.Clone();
        ApplyVariationBand(seedingConfig, plan.SeedTemplate.InitialVariationBand);
        seedingConfig.SpawnChild = Repro.SpawnChildPolicy.SpawnChildNever;

        var children = new List<PopulationMember>(targetChildCount);
        var seenDefinitionShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateDefinitionCount = 0;
        var attempts = 0;
        var maxAttempts = Math.Max(targetChildCount * 4, 8);
        while (children.Count < targetChildCount && attempts < maxAttempts)
        {
            attempts++;
            onProgress?.Invoke(
                "Generating seed variations...",
                $"Bootstrap template {templateIndex}/{templateCount}: attempt {attempts}/{maxAttempts}, accepted {children.Count}/{targetChildCount} varied child seed(s) so far.");
            var remainingRuns = Math.Max(1, targetChildCount - children.Count);
            var seedResult = await _runtimeClient.ReproduceByArtifactsAsync(
                    new Repro.ReproduceByArtifactsRequest
                    {
                        ParentADef = template.Definition.Clone(),
                        ParentBDef = template.Definition.Clone(),
                        StrengthSource = plan.Reproduction.StrengthSource,
                        Config = seedingConfig,
                        Seed = NextSeed(),
                        RunCount = (uint)remainingRuns
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var childDefinition in ExtractChildDefinitions(seedResult))
            {
                if (children.Count >= targetChildCount)
                {
                    break;
                }

                if (!seenDefinitionShas.Add(childDefinition.ToSha256Hex()))
                {
                    duplicateDefinitionCount++;
                    continue;
                }

                var childComplexity = await ResolveDefinitionComplexityAsync(childDefinition, knownShape: null, cancellationToken).ConfigureAwait(false);
                if (!await IsAcceptableInitialSeedAsync(childDefinition, childComplexity, plan.SeedTemplate.InitialSeedShapeConstraints, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                var membership = await CommitArtifactMembershipAsync(
                        childDefinition,
                        new[] { CreateSpeciationParentRef(template.Definition), CreateSpeciationParentRef(template.Definition) },
                        explicitSpeciesId: null,
                        explicitSpeciesDisplayName: null,
                        decisionReason: "basics_seed_child",
                        cancellationToken)
                    .ConfigureAwait(false);
                children.Add(new PopulationMember(
                    childDefinition.Clone(),
                    membership.SpeciesId,
                    membership.SpeciesDisplayName,
                    childComplexity,
                    LastEvaluation: null,
                    CountsTowardOffspringMetrics: true)
                {
                    BootstrapOrigin = CreateBootstrapOrigin(template, exactCopyOrdinal: null)
                });
            }

            onProgress?.Invoke(
                "Generating seed variations...",
                duplicateDefinitionCount > 0
                    ? $"Bootstrap template {templateIndex}/{templateCount}: accepted {children.Count}/{targetChildCount} varied child seed(s) after attempt {attempts}/{maxAttempts}; skipped {duplicateDefinitionCount} duplicate definition(s)."
                    : $"Bootstrap template {templateIndex}/{templateCount}: accepted {children.Count}/{targetChildCount} varied child seed(s) after attempt {attempts}/{maxAttempts}.");
        }

        return children;
    }

    private async Task<PopulationEvaluationResult> EvaluatePopulationAsync(
        BasicsEnvironmentPlan plan,
        IBasicsTaskPlugin taskPlugin,
        int generation,
        IReadOnlyList<PopulationMember> population,
        BasicsOutputObservationMode outputObservationMode,
        BasicsOutputSamplingPolicy outputSamplingPolicy,
        ulong? speciationEpochId,
        ArtifactRef? effectiveTemplateDefinition,
        BasicsResolvedSeedShape? seedShape,
        ulong reproductionCalls,
        ulong reproductionRunsObserved,
        IReadOnlyList<float> offspringAccuracyHistory,
        IReadOnlyList<float> accuracyHistory,
        IReadOnlyList<float> offspringBalancedAccuracyHistory,
        IReadOnlyList<float> balancedAccuracyHistory,
        IReadOnlyList<float> offspringEdgeAccuracyHistory,
        IReadOnlyList<float> offspringInteriorAccuracyHistory,
        IReadOnlyList<float> offspringFitnessHistory,
        IReadOnlyList<float> fitnessHistory,
        float overallBestAccuracy,
        float overallBestFitness,
        BasicsExecutionBestCandidateSummary? overallBestCandidate,
        Action<BasicsExecutionSnapshot>? onSnapshot,
        CancellationToken cancellationToken)
    {
        var evaluated = new List<PopulationMember>(population.Count);
        var batchTimings = new List<BasicsExecutionBatchTimingSummary>();
        var sampleCountPerBrain = taskPlugin.BuildDeterministicDataset().Count * Math.Max(1, outputSamplingPolicy.SampleRepeatCount);
        var maxConcurrent = ResolveEvaluationConcurrency(
            plan.Capacity.RecommendedMaxConcurrentBrains,
            plan.Capacity.EligibleWorkerCount);
        var chunkCount = (int)Math.Ceiling(population.Count / (double)maxConcurrent);
        var setupConcurrency = ResolveSetupConcurrency(plan.Capacity.EligibleWorkerCount, maxConcurrent);
        using var setupGate = new SemaphoreSlim(setupConcurrency, setupConcurrency);
        using var spawnRequestGate = new SemaphoreSlim(setupConcurrency, setupConcurrency);
        var spawnPacer = new SpawnRequestPacer(
            Math.Max(1, plan.Capacity.EligibleWorkerCount),
            _minimumSpawnRequestInterval);

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = population.Skip(chunkIndex * maxConcurrent).Take(maxConcurrent).ToArray();
            var reusedBatchMembers = batch
                .Where(CanReuseEvaluation)
                .Select(static member => member with
                {
                    ActiveBrainId = Guid.Empty,
                    SnapshotArtifact = null
                })
                .ToArray();
            var membersToEvaluate = batch.Where(static member => !CanReuseEvaluation(member)).ToArray();
            var completedBatchTimings = batchTimings.Count == 0 ? null : batchTimings[^1];
            var completedBrains = reusedBatchMembers.Length;
            var completedSamplesByBrain = new ConcurrentDictionary<int, int>();
            var totalSamples = membersToEvaluate.Length * sampleCountPerBrain;
            var progressPublishStopwatch = Stopwatch.StartNew();
            var inFlightBrainProgress = new ConcurrentDictionary<int, InFlightBrainProgress>();

            int ResolveCompletedSamples()
                => completedSamplesByBrain.Values.Sum();

            void PublishBatchProgress(bool force = false)
            {
                if (onSnapshot is null)
                {
                    return;
                }

                if (!force
                    && _evaluationProgressPublishInterval > TimeSpan.Zero
                    && progressPublishStopwatch.Elapsed < _evaluationProgressPublishInterval)
                {
                    return;
                }

                progressPublishStopwatch.Restart();
                onSnapshot(CreateSnapshot(
                    plan.StopCriteria,
                    BasicsExecutionState.Running,
                    $"Evaluating generation {generation}...",
                    BuildBatchProgressDetail(
                        chunkIndex + 1,
                        chunkCount,
                        batch.Length,
                        setupConcurrency,
                        retainedBrainCount: reusedBatchMembers.Length,
                        completedBrains,
                        ResolveCompletedSamples(),
                        totalSamples,
                        activeBrainCount: Math.Max(0, batch.Length - completedBrains),
                        previousBatch: completedBatchTimings,
                        inFlightBrains: inFlightBrainProgress.Values.ToArray()),
                    speciationEpochId,
                    generation,
                    evaluated.Concat(batch).ToArray(),
                    activeBrainCount: Math.Max(0, batch.Length - completedBrains),
                    reproductionCalls,
                    reproductionRunsObserved,
                    effectiveTemplateDefinition,
                    seedShape,
                    accuracyHistory,
                    fitnessHistory,
                    offspringAccuracyHistory: offspringAccuracyHistory,
                    offspringBalancedAccuracyHistory: offspringBalancedAccuracyHistory,
                    balancedAccuracyHistory: balancedAccuracyHistory,
                    offspringEdgeAccuracyHistory: offspringEdgeAccuracyHistory,
                    offspringInteriorAccuracyHistory: offspringInteriorAccuracyHistory,
                    offspringFitnessHistory: offspringFitnessHistory,
                    overallBestAccuracy: overallBestAccuracy,
                    overallBestFitness: overallBestFitness,
                    overallBestCandidate: overallBestCandidate,
                    latestBatchTiming: completedBatchTimings,
                    latestGenerationTiming: BuildGenerationTimingSummary(generation, batchTimings)));
            }

            PublishBatchProgress(force: true);

            var batchStopwatch = Stopwatch.StartNew();
            var batchEvaluations = Array.Empty<MemberEvaluationResult>();
            if (membersToEvaluate.Length > 0)
            {
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var pendingEvaluations = membersToEvaluate
                    .Select((member, memberIndex) => EvaluateMemberAsync(
                        generation,
                        taskPlugin,
                        member,
                        spawnRequestGate,
                        outputObservationMode,
                        outputSamplingPolicy,
                        setupGate,
                        spawnPacer,
                        batchBrainOrdinal: memberIndex + 1,
                        batchBrainCount: membersToEvaluate.Length,
                        onAttemptProgress: progress =>
                        {
                            var forcePublish = !inFlightBrainProgress.TryGetValue(progress.BatchBrainOrdinal, out var previousProgress)
                                               || previousProgress.AttemptNumber != progress.AttemptNumber;
                            inFlightBrainProgress[progress.BatchBrainOrdinal] = progress;
                            PublishBatchProgress(force: forcePublish);
                        },
                        onEvaluationFinished: () =>
                        {
                            inFlightBrainProgress.TryRemove(memberIndex + 1, out _);
                            PublishBatchProgress();
                        },
                        (brainOrdinal, completedSampleCount) =>
                        {
                            completedSamplesByBrain.AddOrUpdate(
                                brainOrdinal,
                                completedSampleCount,
                                (_, current) => Math.Max(current, completedSampleCount));
                            PublishBatchProgress();
                        },
                        batchCts.Token))
                    .ToList();
                var completedEvaluations = new List<MemberEvaluationResult>(membersToEvaluate.Length);
                while (pendingEvaluations.Count > 0)
                {
                    var completedTask = await Task.WhenAny(pendingEvaluations).ConfigureAwait(false);
                    pendingEvaluations.Remove(completedTask);
                    var result = await completedTask.ConfigureAwait(false);
                    completedEvaluations.Add(result);
                    completedBrains++;
                    PublishBatchProgress();

                    if (IsFatalSpawnFailure(result.Member.LastEvaluation))
                    {
                        batchCts.Cancel();
                        try
                        {
                            await Task.WhenAll(pendingEvaluations).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best-effort cancellation of the rest of the batch.
                        }

                        var diagnostic = result.Member.LastEvaluation?.Diagnostics.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                            ?? "spawn_failed";
                        throw new InvalidOperationException($"Generation {generation} batch {chunkIndex + 1} aborted after unrecoverable spawn failure: {diagnostic}");
                    }
                }

                batchEvaluations = completedEvaluations.ToArray();
            }
            var cleanedBatchMembers = await TeardownPopulationBrainsAsync(
                    batchEvaluations.Select(static result => result.Member).ToArray(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            var batchResults = batchEvaluations
                .Zip(cleanedBatchMembers, static (result, cleanedMember) => result with { Member = cleanedMember })
                .ToArray();
            var batchTiming = BuildBatchTimingSummary(generation, chunkIndex + 1, chunkCount, batchResults, batchStopwatch.Elapsed);
            batchTimings.Add(batchTiming);
            evaluated.AddRange(reusedBatchMembers);
            evaluated.AddRange(batchResults.Select(static result => result.Member));

            onSnapshot?.Invoke(CreateSnapshot(
                plan.StopCriteria,
                BasicsExecutionState.Running,
                $"Evaluating generation {generation}...",
                BuildBatchCompletionDetail(batchTiming),
                speciationEpochId,
                generation,
                evaluated.ToArray(),
                activeBrainCount: 0,
                reproductionCalls,
                reproductionRunsObserved,
                    effectiveTemplateDefinition,
                    seedShape,
                    accuracyHistory,
                    fitnessHistory,
                    offspringAccuracyHistory: offspringAccuracyHistory,
                    offspringBalancedAccuracyHistory: offspringBalancedAccuracyHistory,
                    balancedAccuracyHistory: balancedAccuracyHistory,
                    offspringEdgeAccuracyHistory: offspringEdgeAccuracyHistory,
                    offspringInteriorAccuracyHistory: offspringInteriorAccuracyHistory,
                    offspringFitnessHistory: offspringFitnessHistory,
                    overallBestAccuracy: overallBestAccuracy,
                    overallBestFitness: overallBestFitness,
                overallBestCandidate: overallBestCandidate,
                latestBatchTiming: batchTiming,
                latestGenerationTiming: BuildGenerationTimingSummary(generation, batchTimings)));
        }

        return new PopulationEvaluationResult(
            evaluated,
            batchTimings.Count == 0 ? null : batchTimings[^1],
            BuildGenerationTimingSummary(generation, batchTimings));
    }

    private async Task<MemberEvaluationResult> EvaluateMemberAsync(
        int generation,
        IBasicsTaskPlugin taskPlugin,
        PopulationMember member,
        SemaphoreSlim spawnRequestGate,
        BasicsOutputObservationMode outputObservationMode,
        BasicsOutputSamplingPolicy outputSamplingPolicy,
        SemaphoreSlim setupGate,
        SpawnRequestPacer spawnPacer,
        int batchBrainOrdinal,
        int batchBrainCount,
        Action<InFlightBrainProgress>? onAttemptProgress,
        Action? onEvaluationFinished,
        Action<int, int>? onSampleProgress,
        CancellationToken cancellationToken)
    {
        var queueWaitTotal = TimeSpan.Zero;
        var spawnRequestTotal = TimeSpan.Zero;
        var placementWaitTotal = TimeSpan.Zero;
        var setupTotal = TimeSpan.Zero;
        var observationTotal = TimeSpan.Zero;
        var totalElapsed = TimeSpan.Zero;

        MemberEvaluationResult? lastResult = null;
        try
        {
            for (var attempt = 1; attempt <= MaxMemberEvaluationAttempts; attempt++)
            {
                onAttemptProgress?.Invoke(CreateInFlightBrainProgress(
                    member,
                    batchBrainOrdinal,
                    batchBrainCount,
                    attempt,
                    InFlightBrainPhase.WaitingForSpawnSlot));
                var result = await EvaluateMemberAttemptAsync(
                        generation,
                        taskPlugin,
                        member,
                        spawnRequestGate,
                        outputObservationMode,
                        outputSamplingPolicy,
                        setupGate,
                        spawnPacer,
                        attempt,
                        batchBrainOrdinal,
                        batchBrainCount,
                        onAttemptProgress,
                        onSampleProgress,
                        cancellationToken)
                    .ConfigureAwait(false);

                queueWaitTotal += result.Telemetry.QueueWait;
                spawnRequestTotal += result.Telemetry.SpawnRequest;
                placementWaitTotal += result.Telemetry.PlacementWait;
                setupTotal += result.Telemetry.Setup;
                observationTotal += result.Telemetry.Observation;
                totalElapsed += result.Telemetry.Total;
                lastResult = result;

                if (!ShouldRetryMemberEvaluation(result.Member.LastEvaluation, attempt))
                {
                    return CreateMemberEvaluationResult(
                        result.Member,
                        queueWaitTotal,
                        spawnRequestTotal,
                        placementWaitTotal,
                        setupTotal,
                        observationTotal,
                        totalElapsed);
                }

                if (result.Member.ActiveBrainId != Guid.Empty)
                {
                    await TeardownBrainAsync(result.Member.ActiveBrainId, CancellationToken.None).ConfigureAwait(false);
                }

                if (ResolveFailureCategory(result.Member.LastEvaluation) is "spawn_failed" or "spawn_not_placed")
                {
                    await Task.Delay(RetryableSpawnFailureBackoff, cancellationToken).ConfigureAwait(false);
                }
            }

            return CreateMemberEvaluationResult(
                lastResult?.Member ?? member,
                queueWaitTotal,
                spawnRequestTotal,
                placementWaitTotal,
                setupTotal,
                observationTotal,
                totalElapsed);
        }
        finally
        {
            onEvaluationFinished?.Invoke();
        }
    }

    private async Task<MemberEvaluationResult> EvaluateMemberAttemptAsync(
        int generation,
        IBasicsTaskPlugin taskPlugin,
        PopulationMember member,
        SemaphoreSlim spawnRequestGate,
        BasicsOutputObservationMode outputObservationMode,
        BasicsOutputSamplingPolicy outputSamplingPolicy,
        SemaphoreSlim setupGate,
        SpawnRequestPacer spawnPacer,
        int attempt,
        int batchBrainOrdinal,
        int batchBrainCount,
        Action<InFlightBrainProgress>? onAttemptProgress,
        Action<int, int>? onSampleProgress,
        CancellationToken cancellationToken)
    {
        Guid brainId = Guid.Empty;
        var spawnGateHeld = false;
        var setupSlotHeld = false;
        var subscribedVectorOutputs = false;
        var subscribedSingleOutputs = false;
        var totalStopwatch = Stopwatch.StartNew();
        var queueWait = TimeSpan.Zero;
        var spawnRequest = TimeSpan.Zero;
        var placementWait = TimeSpan.Zero;
        var setup = TimeSpan.Zero;
        var observation = TimeSpan.Zero;
        PopulationMember resultMember = member;
        try
        {
            var queueStopwatch = Stopwatch.StartNew();
            await spawnPacer.WaitAsync(cancellationToken).ConfigureAwait(false);
            await spawnRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            queueWait = queueStopwatch.Elapsed;
            spawnGateHeld = true;

            onAttemptProgress?.Invoke(CreateInFlightBrainProgress(
                member,
                batchBrainOrdinal,
                        batchBrainCount,
                        attempt,
                        InFlightBrainPhase.RequestingSpawn));
            var spawnStopwatch = Stopwatch.StartNew();
            SpawnBrainAck? spawnAck;
            try
            {
                    spawnAck = await RequestBrainSpawnAsync(
                        new SpawnBrain
                        {
                            BrainDef = member.Definition.Clone(),
                            LastSnapshot = HasArtifactRef(member.SnapshotArtifact) ? member.SnapshotArtifact!.Clone() : null,
                            InputWidth = taskPlugin.Contract.InputWidth,
                            OutputWidth = taskPlugin.Contract.OutputWidth,
                            StartPaused = true
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                spawnRequest = spawnStopwatch.Elapsed;
                if (spawnGateHeld)
                {
                    spawnRequestGate.Release();
                    spawnGateHeld = false;
                }
            }

            if (spawnAck is not null
                && spawnAck.BrainId.TryToGuid(out brainId)
                && brainId != Guid.Empty
                && string.IsNullOrWhiteSpace(spawnAck.FailureReasonCode)
                && !spawnAck.PlacementReady)
            {
                onAttemptProgress?.Invoke(CreateInFlightBrainProgress(
                    member,
                    batchBrainOrdinal,
                    batchBrainCount,
                    attempt,
                    InFlightBrainPhase.WaitingForPlacement));
                var placementStopwatch = Stopwatch.StartNew();
                spawnAck = await AwaitBrainPlacementAsync(brainId, cancellationToken).ConfigureAwait(false) ?? spawnAck;
                placementWait = placementStopwatch.Elapsed;
            }

            if (spawnAck is null
                || !spawnAck.BrainId.TryToGuid(out brainId)
                || brainId == Guid.Empty
                || !string.IsNullOrWhiteSpace(spawnAck.FailureReasonCode)
                || !spawnAck.PlacementReady)
            {
                var failureCode = string.IsNullOrWhiteSpace(spawnAck?.FailureReasonCode)
                    ? "spawn_not_placed"
                    : spawnAck.FailureReasonCode;
                var failureDetail = TrimDiagnosticDetail(failureCode, spawnAck?.FailureMessage);
                resultMember = member with
                {
                    LastEvaluation = CreateTransportFailure(
                        string.IsNullOrWhiteSpace(failureDetail)
                            ? $"spawn_failed:{failureCode}"
                            : $"spawn_failed:{failureCode}:{failureDetail}"),
                    EvaluationGeneration = generation,
                    ActiveBrainId = brainId
                };
                return CreateMemberEvaluationResult(resultMember, queueWait, spawnRequest, placementWait, setup, observation, totalStopwatch.Elapsed);
            }

            _trackedBrains.TryAdd(brainId, 0);

            var setupStopwatch = Stopwatch.StartNew();
            onAttemptProgress?.Invoke(CreateInFlightBrainProgress(
                member,
                batchBrainOrdinal,
                batchBrainCount,
                attempt,
                InFlightBrainPhase.WaitingForSetupSlot));
            await setupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            setupSlotHeld = true;

            onAttemptProgress?.Invoke(CreateInFlightBrainProgress(
                member,
                batchBrainOrdinal,
                batchBrainCount,
                attempt,
                InFlightBrainPhase.ConfiguringRuntime));
            var brainInfo = await _runtimeClient.RequestBrainInfoAsync(brainId, cancellationToken).ConfigureAwait(false);
            var geometry = BasicsIoGeometry.Validate(brainInfo);
            if (!geometry.IsValid)
            {
                resultMember = member with
                {
                    LastEvaluation = CreateTransportFailure($"geometry_invalid:{geometry.FailureReason}"),
                    EvaluationGeneration = generation,
                    ActiveBrainId = brainId
                };
                setup = setupStopwatch.Elapsed;
                return CreateMemberEvaluationResult(resultMember, queueWait, spawnRequest, placementWait, setup, observation, totalStopwatch.Elapsed);
            }

            await ConfigureBrainEvaluationRuntimeStateAsync(brainId, cancellationToken).ConfigureAwait(false);
            await ConfigureBrainOutputObservationModeAsync(brainId, outputObservationMode, brainInfo, cancellationToken).ConfigureAwait(false);
            await SynchronizeBrainEvaluationRuntimeConfigAsync(brainId, cancellationToken).ConfigureAwait(false);

            await _runtimeClient.SubscribeOutputsVectorAsync(brainId, cancellationToken).ConfigureAwait(false);
            _runtimeClient.ResetOutputBuffer(brainId);
            subscribedVectorOutputs = true;

            if (outputObservationMode == BasicsOutputObservationMode.EventedOutput)
            {
                await _runtimeClient.SubscribeOutputsAsync(brainId, cancellationToken).ConfigureAwait(false);
                _runtimeClient.ResetOutputEventBuffer(brainId);
                subscribedSingleOutputs = true;
            }
            setup = setupStopwatch.Elapsed;

            setupGate.Release();
            setupSlotHeld = false;

            var observationStopwatch = Stopwatch.StartNew();
            onAttemptProgress?.Invoke(CreateInFlightBrainProgress(
                member,
                batchBrainOrdinal,
                batchBrainCount,
                attempt,
                InFlightBrainPhase.EvaluatingSamples));

            // Spawned evaluation brains start paused. Prime the output path while still paused so
            // the first scored sample does not race either cold resume or sample-input delivery.
            var activationFailure = await PrimeOutputObservationAsync(
                    brainId,
                    taskPlugin.Contract.InputWidth,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(activationFailure))
            {
                resultMember = member with
                {
                    LastEvaluation = CreateTransportFailure($"output_timeout_or_width_mismatch:{activationFailure}"),
                    EvaluationGeneration = generation,
                    ActiveBrainId = brainId
                };
                observation = observationStopwatch.Elapsed;
                return CreateMemberEvaluationResult(resultMember, queueWait, spawnRequest, placementWait, setup, observation, totalStopwatch.Elapsed);
            }

            var observations = new List<BasicsTaskObservation>();
            var samples = taskPlugin.BuildDeterministicDataset();
            ulong lastTick = 0;
            var sampleRepeatCount = Math.Max(1, outputSamplingPolicy.SampleRepeatCount);
            var completedObservationAttempts = 0;

            foreach (var sample in samples)
            {
                var repeatedObservations = new List<BasicsTaskObservation>(sampleRepeatCount);
                for (var repeatIndex = 0; repeatIndex < sampleRepeatCount; repeatIndex++)
                {
                    var sampleObservation = await ObserveSampleAsync(
                            brainId,
                            sample,
                            lastTick,
                            outputObservationMode,
                            outputSamplingPolicy,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (sampleObservation.Observation is null)
                    {
                        var failureDetail = string.IsNullOrWhiteSpace(sampleObservation.FailureDetail)
                            ? "output_timeout_or_width_mismatch"
                            : $"output_timeout_or_width_mismatch:{sampleObservation.FailureDetail}";
                        resultMember = member with
                        {
                            LastEvaluation = CreateTransportFailure(failureDetail),
                            EvaluationGeneration = generation,
                            ActiveBrainId = brainId
                        };
                        observation = observationStopwatch.Elapsed;
                        return CreateMemberEvaluationResult(resultMember, queueWait, spawnRequest, placementWait, setup, observation, totalStopwatch.Elapsed);
                    }

                    repeatedObservations.Add(sampleObservation.Observation.Value);
                    completedObservationAttempts++;
                    onSampleProgress?.Invoke(batchBrainOrdinal, completedObservationAttempts);
                    lastTick = sampleObservation.Observation.Value.TickId;
                }

                observations.Add(AggregateRepeatedObservations(repeatedObservations));
            }
            observation = observationStopwatch.Elapsed;

            var rawEvaluation = taskPlugin.Evaluate(
                new BasicsTaskEvaluationContext(
                    taskPlugin.Contract.InputWidth,
                    taskPlugin.Contract.OutputWidth,
                    TickAligned: taskPlugin.Contract.UsesTickAlignedEvaluation,
                    TickBase: observations.Count == 0 ? 0UL : observations[0].TickId),
                samples,
                observations);
            var evaluation = ApplyReadyTickMetrics(rawEvaluation, observations);
            resultMember = member with
            {
                LastEvaluation = evaluation,
                EvaluationGeneration = generation,
                ActiveBrainId = brainId
            };
            return CreateMemberEvaluationResult(resultMember, queueWait, spawnRequest, placementWait, setup, observation, totalStopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            if (brainId != Guid.Empty)
            {
                try
                {
                    await TeardownBrainAsync(brainId, CancellationToken.None).ConfigureAwait(false);
                    brainId = Guid.Empty;
                }
                catch
                {
                    // Best-effort cancellation cleanup only.
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            resultMember = member with
            {
                LastEvaluation = CreateTransportFailure($"evaluation_failed:{ex.GetBaseException().Message}"),
                EvaluationGeneration = generation,
                ActiveBrainId = brainId
            };
            return CreateMemberEvaluationResult(resultMember, queueWait, spawnRequest, placementWait, setup, observation, totalStopwatch.Elapsed);
        }
        finally
        {
            if (spawnGateHeld)
            {
                spawnRequestGate.Release();
            }

            if (setupSlotHeld)
            {
                setupGate.Release();
            }

            if (brainId != Guid.Empty)
            {
                try
                {
                    if (subscribedSingleOutputs)
                    {
                        await _runtimeClient.UnsubscribeOutputsAsync(brainId, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (subscribedVectorOutputs)
                    {
                        await _runtimeClient.UnsubscribeOutputsVectorAsync(brainId, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Best-effort unsubscribe only.
                }
            }
        }
    }

    private static bool ShouldRetryMemberEvaluation(
        BasicsTaskEvaluationResult? evaluation,
        int attempt)
    {
        if (attempt >= MaxMemberEvaluationAttempts || evaluation is null || evaluation.Diagnostics.Count == 0)
        {
            return false;
        }

        var failureCategory = ResolveFailureCategory(evaluation);
        return (failureCategory is "spawn_failed" or "spawn_not_placed"
                && IsRetryableSpawnFailure(evaluation, attempt))
               || failureCategory is "evaluation_failed"
               || IsRetryableOutputTimeout(evaluation);
    }

    private static bool IsRetryableSpawnFailure(BasicsTaskEvaluationResult evaluation, int attempt)
    {
        var diagnostic = evaluation.Diagnostics.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return false;
        }

        if (diagnostic.Contains("spawn_internal_error", StringComparison.Ordinal))
        {
            return false;
        }

        if (diagnostic.Contains("spawn_request_canceled", StringComparison.Ordinal)
            || diagnostic.Contains("spawn_brain_info_timeout", StringComparison.Ordinal))
        {
            return attempt < 2;
        }

        return true;
    }

    private static bool IsRetryableOutputTimeout(BasicsTaskEvaluationResult evaluation)
    {
        var diagnostic = evaluation.Diagnostics.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(diagnostic)
            || !diagnostic.StartsWith("output_timeout_or_width_mismatch:", StringComparison.Ordinal))
        {
            return false;
        }

        return !diagnostic.Contains("ready_window_exhausted", StringComparison.Ordinal);
    }

    private static bool IsFatalSpawnFailure(BasicsTaskEvaluationResult? evaluation)
    {
        if (evaluation is null)
        {
            return false;
        }

        var failureCategory = ResolveFailureCategory(evaluation);
        if (failureCategory is not ("spawn_failed" or "spawn_not_placed"))
        {
            return false;
        }

        var diagnostic = evaluation.Diagnostics.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return false;
        }

        return diagnostic.Contains("spawn_unavailable", StringComparison.Ordinal)
               || diagnostic.Contains("spawn_request_failed", StringComparison.Ordinal)
               || diagnostic.Contains("spawn_empty_response", StringComparison.Ordinal)
               || diagnostic.Contains("spawn_invalid_request", StringComparison.Ordinal);
    }

    private static int ResolveSetupConcurrency(int eligibleWorkerCount, int maxConcurrent)
    {
        return Math.Max(1, Math.Min(maxConcurrent, Math.Max(1, eligibleWorkerCount)));
    }

    private static int ResolveEvaluationConcurrency(int recommendedMaxConcurrentBrains, int eligibleWorkerCount)
    {
        var recommended = Math.Max(1, recommendedMaxConcurrentBrains);
        if (eligibleWorkerCount <= 0)
        {
            return recommended;
        }

        return Math.Max(1, Math.Min(recommended, eligibleWorkerCount));
    }

    private sealed class SpawnRequestPacer
    {
        private readonly TimeSpan _minimumInterval;
        private readonly DateTimeOffset[] _laneNextAllowedAt;
        private readonly object _lock = new();

        public SpawnRequestPacer(int laneCount, TimeSpan minimumInterval)
        {
            _minimumInterval = minimumInterval <= TimeSpan.Zero ? TimeSpan.Zero : minimumInterval;
            _laneNextAllowedAt = Enumerable.Repeat(DateTimeOffset.MinValue, Math.Max(1, laneCount)).ToArray();
        }

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (_minimumInterval <= TimeSpan.Zero)
            {
                return;
            }

            TimeSpan delay;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                var selectedLane = 0;
                for (var lane = 1; lane < _laneNextAllowedAt.Length; lane++)
                {
                    if (_laneNextAllowedAt[lane] < _laneNextAllowedAt[selectedLane])
                    {
                        selectedLane = lane;
                    }
                }

                var earliest = _laneNextAllowedAt[selectedLane];
                delay = earliest > now ? earliest - now : TimeSpan.Zero;
                var scheduledStart = delay > TimeSpan.Zero ? earliest : now;
                _laneNextAllowedAt[selectedLane] = scheduledStart + _minimumInterval;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static MemberEvaluationResult CreateMemberEvaluationResult(
        PopulationMember member,
        TimeSpan queueWait,
        TimeSpan spawnRequest,
        TimeSpan placementWait,
        TimeSpan setup,
        TimeSpan observation,
        TimeSpan total)
        => new(
            member,
            new MemberEvaluationTelemetry(
                queueWait,
                spawnRequest,
                placementWait,
                setup,
                observation,
                total,
                ResolveFailureCategory(member.LastEvaluation)));

    private static string BuildBatchStartDetail(
        int batchIndex,
        int batchCount,
        int brainCount,
        int setupConcurrency,
        BasicsExecutionBatchTimingSummary? previousBatch)
    {
        var detail = $"Batch {batchIndex}/{batchCount} with {brainCount} active brain(s). Setup concurrency {setupConcurrency}.";
        if (previousBatch is null)
        {
            return detail;
        }

        return $"{detail} Previous batch {FormatSeconds(previousBatch.BatchDurationSeconds)} total; queue {FormatSeconds(previousBatch.AverageQueueWaitSeconds)}/brain, spawn {FormatSeconds(previousBatch.AverageSpawnRequestSeconds)}/brain, placement {FormatSeconds(previousBatch.AveragePlacementWaitSeconds)}/brain, setup {FormatSeconds(previousBatch.AverageSetupSeconds)}/brain, observe {FormatSeconds(previousBatch.AverageObservationSeconds)}/brain.";
    }

    private static string BuildBatchCompletionDetail(BasicsExecutionBatchTimingSummary timing)
    {
        var detail = $"Batch {timing.BatchIndex}/{timing.BatchCount} finished in {FormatSeconds(timing.BatchDurationSeconds)}; queue {FormatSeconds(timing.AverageQueueWaitSeconds)}/brain, spawn {FormatSeconds(timing.AverageSpawnRequestSeconds)}/brain, placement {FormatSeconds(timing.AveragePlacementWaitSeconds)}/brain, setup {FormatSeconds(timing.AverageSetupSeconds)}/brain, observe {FormatSeconds(timing.AverageObservationSeconds)}/brain.";
        if (!string.IsNullOrWhiteSpace(timing.FailureSummary))
        {
            detail = $"{detail} Failures: {timing.FailureSummary}.";
        }

        return detail;
    }

    private static string BuildBatchProgressDetail(
        int batchIndex,
        int batchCount,
        int batchBrainCount,
        int setupConcurrency,
        int retainedBrainCount,
        int completedBrains,
        int completedSamples,
        int totalSamples,
        int activeBrainCount,
        BasicsExecutionBatchTimingSummary? previousBatch,
        IReadOnlyList<InFlightBrainProgress>? inFlightBrains)
    {
        var newlyEvaluatedTotal = Math.Max(0, batchBrainCount - retainedBrainCount);
        var newlyEvaluatedCompleted = Math.Max(0, completedBrains - retainedBrainCount);
        var detail = $"Batch {batchIndex}/{batchCount}: brains {completedBrains}/{batchBrainCount} ready ({retainedBrainCount} retained, {newlyEvaluatedCompleted}/{newlyEvaluatedTotal} newly evaluated), samples {completedSamples}/{totalSamples}, active {activeBrainCount}. Setup concurrency {setupConcurrency}.";
        var inFlightDetail = BuildInFlightBrainProgressDetail(inFlightBrains);
        if (!string.IsNullOrWhiteSpace(inFlightDetail))
        {
            detail = $"{detail} {inFlightDetail}";
        }

        if (previousBatch is null)
        {
            return detail;
        }

        return $"{detail} Previous batch {FormatSeconds(previousBatch.BatchDurationSeconds)} total; queue {FormatSeconds(previousBatch.AverageQueueWaitSeconds)}/brain, spawn {FormatSeconds(previousBatch.AverageSpawnRequestSeconds)}/brain, placement {FormatSeconds(previousBatch.AveragePlacementWaitSeconds)}/brain, setup {FormatSeconds(previousBatch.AverageSetupSeconds)}/brain, observe {FormatSeconds(previousBatch.AverageObservationSeconds)}/brain.";
    }

    private static string BuildInFlightBrainProgressDetail(IReadOnlyList<InFlightBrainProgress>? inFlightBrains)
    {
        if (inFlightBrains is null || inFlightBrains.Count == 0)
        {
            return string.Empty;
        }

        var ordered = inFlightBrains
            .OrderBy(progress => progress.BatchBrainOrdinal)
            .ThenBy(progress => progress.AttemptNumber)
            .ToArray();
        var displayed = ordered
            .Take(3)
            .Select(progress =>
                $"brain {progress.BatchBrainOrdinal}/{progress.BatchBrainCount} sha {progress.DefinitionShaShort} attempt {progress.AttemptNumber}/{progress.MaxAttempts} {DescribeInFlightBrainPhase(progress.Phase)}")
            .ToArray();
        var suffix = ordered.Length > displayed.Length
            ? $" +{ordered.Length - displayed.Length} more"
            : string.Empty;
        return $"Active work: {string.Join("; ", displayed)}{suffix}.";
    }

    private static string DescribeInFlightBrainPhase(InFlightBrainPhase phase)
        => phase switch
        {
            InFlightBrainPhase.WaitingForSpawnSlot => "waiting for local spawn gate",
            InFlightBrainPhase.RequestingSpawn => "requesting spawn placement",
            InFlightBrainPhase.WaitingForPlacement => "waiting for placement visibility",
            InFlightBrainPhase.WaitingForSetupSlot => "waiting for runtime setup slot",
            InFlightBrainPhase.ConfiguringRuntime => "configuring runtime",
            InFlightBrainPhase.EvaluatingSamples => "evaluating samples",
            _ => "working"
        };

    private static BasicsExecutionBatchTimingSummary BuildBatchTimingSummary(
        int generation,
        int batchIndex,
        int batchCount,
        IReadOnlyList<MemberEvaluationResult> results,
        TimeSpan batchDuration)
    {
        if (results.Count == 0)
        {
            return new BasicsExecutionBatchTimingSummary(
                generation,
                batchIndex,
                batchCount,
                0,
                0,
                0,
                batchDuration.TotalSeconds,
                0d,
                0d,
                0d,
                0d,
                0d,
                string.Empty);
        }

        var failures = results
            .Select(result => result.Telemetry.FailureCategory)
            .Where(static value => !string.Equals(value, "success", StringComparison.Ordinal))
            .GroupBy(static value => value, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => $"{group.Key} x{group.Count()}")
            .ToArray();

        return new BasicsExecutionBatchTimingSummary(
            generation,
            batchIndex,
            batchCount,
            results.Count,
            results.Count(static result => string.Equals(result.Telemetry.FailureCategory, "success", StringComparison.Ordinal)),
            results.Count(static result => !string.Equals(result.Telemetry.FailureCategory, "success", StringComparison.Ordinal)),
            batchDuration.TotalSeconds,
            results.Average(static result => result.Telemetry.QueueWait.TotalSeconds),
            results.Average(static result => result.Telemetry.SpawnRequest.TotalSeconds),
            results.Average(static result => result.Telemetry.PlacementWait.TotalSeconds),
            results.Average(static result => result.Telemetry.Setup.TotalSeconds),
            results.Average(static result => result.Telemetry.Observation.TotalSeconds),
            string.Join("; ", failures));
    }

    private static BasicsExecutionGenerationTimingSummary? BuildGenerationTimingSummary(
        int generation,
        IReadOnlyList<BasicsExecutionBatchTimingSummary> batchTimings)
    {
        if (batchTimings.Count == 0)
        {
            return null;
        }

        return new BasicsExecutionGenerationTimingSummary(
            generation,
            batchTimings.Count,
            batchTimings.Sum(static timing => timing.BrainCount),
            batchTimings.Sum(static timing => timing.SuccessfulBrainCount),
            batchTimings.Sum(static timing => timing.FailedBrainCount),
            batchTimings.Sum(static timing => timing.BatchDurationSeconds),
            batchTimings.Average(static timing => timing.BatchDurationSeconds),
            batchTimings.Average(static timing => timing.AverageQueueWaitSeconds),
            batchTimings.Average(static timing => timing.AverageSpawnRequestSeconds),
            batchTimings.Average(static timing => timing.AveragePlacementWaitSeconds),
            batchTimings.Average(static timing => timing.AverageSetupSeconds),
            batchTimings.Average(static timing => timing.AverageObservationSeconds),
            string.Join(
                "; ",
                batchTimings
                    .Select(static timing => timing.FailureSummary)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)));
    }

    private static string ResolveFailureCategory(BasicsTaskEvaluationResult? evaluation)
    {
        if (evaluation is null || evaluation.Diagnostics.Count == 0)
        {
            return "success";
        }

        var diagnostic = evaluation.Diagnostics[0];
        if (diagnostic.StartsWith("output_timeout_or_width_mismatch:", StringComparison.Ordinal))
        {
            if (diagnostic.Contains("ready_window_exhausted", StringComparison.Ordinal))
            {
                return "ready_timeout";
            }

            if (diagnostic.Contains("width_mismatch", StringComparison.Ordinal))
            {
                return "output_width_mismatch";
            }

            return "output_timeout";
        }

        var delimiter = diagnostic.IndexOf(':');
        return delimiter <= 0 ? diagnostic : diagnostic[..delimiter];
    }

    private static bool CanReuseEvaluation(PopulationMember member)
        => member.LastEvaluation is not null && member.LastEvaluation.Diagnostics.Count == 0;

    private static string FormatSeconds(double seconds)
        => seconds >= 10d
            ? $"{seconds:0.0}s"
            : $"{seconds:0.00}s";

    private async Task<ObservationAttemptResult> ObserveSampleAsync(
        Guid brainId,
        BasicsTaskSample sample,
        ulong lastTick,
        BasicsOutputObservationMode outputObservationMode,
        BasicsOutputSamplingPolicy outputSamplingPolicy,
        CancellationToken cancellationToken)
    {
        var pauseAck = await _runtimeClient.PauseBrainAsync(
                brainId,
                reason: "basics_sample_boundary",
                cancellationToken)
            .ConfigureAwait(false);
        ValidateIoCommandAck(pauseAck, brainId, "pause_brain");

        await ResetBrainRuntimeStateForSampleAsync(brainId, cancellationToken).ConfigureAwait(false);

        _runtimeClient.ResetOutputBuffer(brainId);
        _runtimeClient.ResetOutputEventBuffer(brainId);

        await _runtimeClient.SendInputVectorAsync(
                brainId,
                new[] { sample.InputA, sample.InputB },
                cancellationToken)
            .ConfigureAwait(false);

        var resumeAck = await _runtimeClient.ResumeBrainAsync(brainId, cancellationToken).ConfigureAwait(false);
        ValidateIoCommandAck(resumeAck, brainId, "resume_brain");

        return outputObservationMode == BasicsOutputObservationMode.EventedOutput
            ? await WaitForReadyEventObservationAsync(
                    brainId,
                    lastTick,
                    outputSamplingPolicy,
                    cancellationToken)
                .ConfigureAwait(false)
            : await WaitForReadyVectorObservationAsync(
                    brainId,
                    lastTick,
                    outputObservationMode,
                    outputSamplingPolicy,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<string?> PrimeOutputObservationAsync(
        Guid brainId,
        uint inputWidth,
        CancellationToken cancellationToken)
    {
        if (brainId == Guid.Empty || inputWidth == 0)
        {
            return null;
        }

        await ResetBrainRuntimeStateForSampleAsync(brainId, cancellationToken).ConfigureAwait(false);

        _runtimeClient.ResetOutputBuffer(brainId);
        _runtimeClient.ResetOutputEventBuffer(brainId);
        await _runtimeClient.SendInputVectorAsync(
                brainId,
                CreateNeutralInputVector(inputWidth),
                cancellationToken)
            .ConfigureAwait(false);

        var resumeAck = await _runtimeClient.ResumeBrainAsync(brainId, cancellationToken).ConfigureAwait(false);
        ValidateIoCommandAck(resumeAck, brainId, "resume_brain");

        var output = await _runtimeClient.WaitForOutputVectorAsync(
                brainId,
                afterTickExclusive: 0,
                InitialBrainActivationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var failureDetail = ClassifyObservationVectorFailure(output, 0, lastTick: 0);
        if (failureDetail is not null)
        {
            return $"startup_{failureDetail}";
        }
        return null;
    }

    private async Task<ObservationAttemptResult> WaitForReadyVectorObservationAsync(
        Guid brainId,
        ulong afterTickExclusive,
        BasicsOutputObservationMode outputObservationMode,
        BasicsOutputSamplingPolicy outputSamplingPolicy,
        CancellationToken cancellationToken)
    {
        var tickCursor = afterTickExclusive;
        var vectorsSeen = 0;
        for (var observedTicks = 0; observedTicks < outputSamplingPolicy.MaxReadyWindowTicks; observedTicks++)
        {
            var output = await _runtimeClient.WaitForOutputVectorAsync(
                    brainId,
                    tickCursor,
                    ResolveObservationTimeout(outputObservationMode),
                    cancellationToken)
                .ConfigureAwait(false);
            var failureDetail = ClassifyObservationVectorFailure(output, vectorsSeen, lastTick: tickCursor);
            if (failureDetail is not null)
            {
                return new ObservationAttemptResult(
                    null,
                    failureDetail);
            }

            tickCursor = output!.TickId;
            vectorsSeen++;
            if (output.Values[(int)ReadyOutputIndex] >= outputSamplingPolicy.VectorReadyThreshold)
            {
                return new ObservationAttemptResult(CreateObservation(output, vectorsSeen), null);
            }
        }

        return new ObservationAttemptResult(
            null,
            $"ready_window_exhausted:vectors_seen={vectorsSeen}:last_tick={tickCursor}");
    }

    private async Task<ObservationAttemptResult> WaitForReadyEventObservationAsync(
        Guid brainId,
        ulong afterTickExclusive,
        BasicsOutputSamplingPolicy outputSamplingPolicy,
        CancellationToken cancellationToken)
    {
        var vectorCursor = afterTickExclusive;
        var readyEventCursor = afterTickExclusive;
        var observedTicks = 0;
        var readyEventsSeen = 0;
        var vectorsByTick = new Dictionary<ulong, BasicsRuntimeOutputVector>();
        var readyTicks = new HashSet<ulong>();
        using var observationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observationToken = observationCts.Token;
        var timeout = ResolveObservationTimeout(BasicsOutputObservationMode.EventedOutput);
        var nextVectorTask = _runtimeClient.WaitForOutputVectorAsync(brainId, vectorCursor, timeout, observationToken);
        var nextReadyEventTask = _runtimeClient.WaitForOutputEventAsync(
            brainId,
            readyEventCursor,
            timeout,
            ReadyOutputIndex,
            observationToken);

        try
        {
            while (observedTicks < outputSamplingPolicy.MaxReadyWindowTicks)
            {
                if (nextReadyEventTask.IsCompleted)
                {
                    var readyEvent = await nextReadyEventTask.ConfigureAwait(false);
                    if (readyEvent is not null)
                    {
                        readyEventsSeen++;
                        readyEventCursor = Math.Max(readyEventCursor, readyEvent.TickId);
                        if (vectorsByTick.TryGetValue(readyEvent.TickId, out var readyVector))
                        {
                            if (readyVector.Values[(int)ReadyOutputIndex] >= outputSamplingPolicy.VectorReadyThreshold)
                            {
                                return new ObservationAttemptResult(
                                    CreateObservation(readyVector, CountReadyTicksThrough(vectorsByTick, readyEvent.TickId)),
                                    null);
                            }

                            readyTicks.Remove(readyEvent.TickId);
                        }

                        readyTicks.Add(readyEvent.TickId);
                    }

                    nextReadyEventTask = _runtimeClient.WaitForOutputEventAsync(
                        brainId,
                        readyEventCursor,
                        timeout,
                        ReadyOutputIndex,
                        observationToken);

                    if (!nextVectorTask.IsCompleted)
                    {
                        continue;
                    }
                }

                if (!nextVectorTask.IsCompleted)
                {
                    var completedTask = await Task.WhenAny(nextVectorTask, nextReadyEventTask).ConfigureAwait(false);
                    if (completedTask == nextReadyEventTask)
                    {
                        continue;
                    }
                }

                var output = await nextVectorTask.ConfigureAwait(false);
                var failureDetail = ClassifyObservationVectorFailure(
                    output,
                    vectorsByTick.Count,
                    readyEventsSeen,
                    vectorCursor,
                    readyEventCursor);
                if (failureDetail is not null)
                {
                    return new ObservationAttemptResult(
                        null,
                        failureDetail);
                }

                vectorsByTick[output!.TickId] = output;
                vectorCursor = output.TickId;
                observedTicks++;
                if (readyTicks.Contains(output.TickId))
                {
                    if (output.Values[(int)ReadyOutputIndex] >= outputSamplingPolicy.VectorReadyThreshold)
                    {
                        return new ObservationAttemptResult(CreateObservation(output, observedTicks), null);
                    }

                    readyTicks.Remove(output.TickId);
                }

                if (observedTicks < outputSamplingPolicy.MaxReadyWindowTicks)
                {
                    nextVectorTask = _runtimeClient.WaitForOutputVectorAsync(
                        brainId,
                        vectorCursor,
                        timeout,
                        observationToken);
                }
            }

            return new ObservationAttemptResult(
                null,
                $"ready_window_exhausted:vectors_seen={vectorsByTick.Count}:ready_events_seen={readyEventsSeen}:last_vector_tick={vectorCursor}:last_ready_tick={readyEventCursor}");
        }
        finally
        {
            observationCts.Cancel();
        }
    }

    private static string? ClassifyObservationVectorFailure(
        BasicsRuntimeOutputVector? output,
        int vectorsSeen,
        int readyEventsSeen = 0,
        ulong lastTick = 0,
        ulong lastReadyTick = 0)
    {
        if (output is null)
        {
            return readyEventsSeen > 0
                ? $"vector_missing:vectors_seen={vectorsSeen}:ready_events_seen={readyEventsSeen}:last_vector_tick={lastTick}:last_ready_tick={lastReadyTick}"
                : $"vector_missing:vectors_seen={vectorsSeen}:last_tick={lastTick}";
        }

        if (output.Values.Count < (int)BasicsIoGeometry.OutputWidth)
        {
            return readyEventsSeen > 0
                ? $"width_mismatch:observed_width={output.Values.Count}:expected_width={(int)BasicsIoGeometry.OutputWidth}:vectors_seen={vectorsSeen}:ready_events_seen={readyEventsSeen}:last_vector_tick={output.TickId}:last_ready_tick={lastReadyTick}"
                : $"width_mismatch:observed_width={output.Values.Count}:expected_width={(int)BasicsIoGeometry.OutputWidth}:vectors_seen={vectorsSeen}:last_tick={output.TickId}";
        }

        return null;
    }

    private static float[] CreateNeutralInputVector(uint inputWidth)
        => new float[checked((int)inputWidth)];

    private static BasicsTaskObservation CreateObservation(BasicsRuntimeOutputVector output, int readyTickCount)
        => new(
            output.TickId,
            output.Values[(int)ValueOutputIndex],
            checked((float)Math.Max(1, readyTickCount)));

    private static int CountReadyTicksThrough(
        IReadOnlyDictionary<ulong, BasicsRuntimeOutputVector> vectorsByTick,
        ulong readyTickId)
    {
        var count = 0;
        foreach (var tickId in vectorsByTick.Keys)
        {
            if (tickId <= readyTickId)
            {
                count++;
            }
        }

        return Math.Max(1, count);
    }

    private static BasicsTaskObservation AggregateRepeatedObservations(IReadOnlyList<BasicsTaskObservation> observations)
    {
        if (observations.Count == 0)
        {
            return default;
        }

        if (observations.Count == 1)
        {
            return observations[0];
        }

        return new BasicsTaskObservation(
            TickId: observations[^1].TickId,
            OutputValue: observations.Average(static observation => observation.OutputValue),
            ReadyTickCount: observations.Average(static observation => observation.ReadyTickCount));
    }

    private static BasicsTaskEvaluationResult ApplyReadyTickMetrics(
        BasicsTaskEvaluationResult evaluation,
        IReadOnlyList<BasicsTaskObservation> observations)
    {
        var readyTicks = observations
            .Select(static observation => observation.ReadyTickCount)
            .Where(static readyTickCount => float.IsFinite(readyTickCount) && readyTickCount > 0f)
            .OrderBy(static readyTickCount => readyTickCount)
            .ToArray();
        if (readyTicks.Length == 0)
        {
            return evaluation;
        }

        return evaluation with
        {
            AverageReadyTickCount = readyTicks.Average(),
            MinReadyTickCount = readyTicks[0],
            MedianReadyTickCount = ComputeMedian(readyTicks),
            MaxReadyTickCount = readyTicks[^1],
            ReadyTickStdDev = ComputeStandardDeviation(readyTicks)
        };
    }

    private static float ComputeMedian(IReadOnlyList<float> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return 0f;
        }

        var mid = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[mid - 1] + sortedValues[mid]) / 2f
            : sortedValues[mid];
    }

    private static float ComputeStandardDeviation(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
        {
            return 0f;
        }

        var mean = values.Average();
        var variance = values
            .Select(value =>
            {
                var delta = value - mean;
                return delta * delta;
            })
            .Average();
        return (float)Math.Sqrt(variance);
    }

    private static TimeSpan ResolveObservationTimeout(BasicsOutputObservationMode mode)
        => mode == BasicsOutputObservationMode.EventedOutput
            ? EventedObservationTimeout
            : VectorObservationTimeout;

    private async Task WaitForBrainTerminationAsync(Guid brainId, CancellationToken cancellationToken)
    {
        var terminated = await _runtimeClient.WaitForBrainTerminatedAsync(
                brainId,
                BrainTeardownTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (terminated?.BrainId?.TryToGuid(out var terminatedBrainId) == true && terminatedBrainId == brainId)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(BrainTeardownTimeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            var info = await _runtimeClient.RequestBrainInfoAsync(brainId, timeoutCts.Token).ConfigureAwait(false);
            if (info is null || info.InputWidth == 0 || info.OutputWidth == 0)
            {
                return;
            }

            await Task.Delay(BrainTeardownPollInterval, timeoutCts.Token).ConfigureAwait(false);
        }
    }

    private async Task<List<PopulationMember>> RetainWinningCandidateAsync(
        IReadOnlyList<PopulationMember> population,
        BasicsExecutionBestCandidateSummary? winner,
        CancellationToken cancellationToken)
    {
        if (winner?.ActiveBrainId is not Guid retainedBrainId || retainedBrainId == Guid.Empty)
        {
            return await TeardownPopulationBrainsAsync(population, cancellationToken).ConfigureAwait(false);
        }

        var snapshotArtifact = await TryRequestSnapshotArtifactAsync(retainedBrainId, cancellationToken).ConfigureAwait(false);
        var updated = new List<PopulationMember>(population.Count);
        foreach (var member in population)
        {
            if (member.ActiveBrainId == retainedBrainId)
            {
                _trackedBrains.TryRemove(retainedBrainId, out _);
                updated.Add(member with
                {
                    SnapshotArtifact = snapshotArtifact?.Clone()
                });
                continue;
            }

            if (member.ActiveBrainId != Guid.Empty)
            {
                await TeardownBrainAsync(member.ActiveBrainId, cancellationToken).ConfigureAwait(false);
            }

            updated.Add(member with
            {
                ActiveBrainId = Guid.Empty,
                SnapshotArtifact = null
            });
        }

        return updated;
    }

    private async Task<(List<PopulationMember> Population, BasicsExecutionBestCandidateSummary? BestCandidate)> TryRetainBestCandidateForExportAsync(
        BasicsTaskContract taskContract,
        IReadOnlyList<PopulationMember> population,
        BasicsExecutionBestCandidateSummary? currentGenerationBest,
        BasicsExecutionBestCandidateSummary? bestCandidateSoFar,
        CancellationToken cancellationToken)
    {
        if (!IsViableBestCandidate(bestCandidateSoFar))
        {
            var cleaned = await TeardownPopulationBrainsAsync(population, cancellationToken).ConfigureAwait(false);
            return (cleaned, null);
        }

        if (currentGenerationBest is not null
            && currentGenerationBest.ActiveBrainId is Guid retainedBrainId
            && retainedBrainId != Guid.Empty
            && HasSameDefinitionArtifact(currentGenerationBest.DefinitionArtifact, bestCandidateSoFar!.DefinitionArtifact))
        {
            var retainedPopulation = await RetainWinningCandidateAsync(population, currentGenerationBest, cancellationToken).ConfigureAwait(false);
            var retainedBestCandidate = BuildGenerationMetrics(retainedPopulation, stopCriteria: null, includeWinnerRuntimeState: true).BestCandidate;
            return (retainedPopulation, retainedBestCandidate);
        }

        await TeardownPopulationBrainsAsync(population, cancellationToken).ConfigureAwait(false);

        var respawned = await RespawnBestCandidateForExportAsync(taskContract, bestCandidateSoFar!, cancellationToken).ConfigureAwait(false);
        if (respawned is null)
        {
            return (new List<PopulationMember>(), CloneBestCandidate(bestCandidateSoFar));
        }

        return (new List<PopulationMember> { respawned.Value.Member }, respawned.Value.Summary);
    }

    private async Task<(PopulationMember Member, BasicsExecutionBestCandidateSummary Summary)?> RespawnBestCandidateForExportAsync(
        BasicsTaskContract taskContract,
        BasicsExecutionBestCandidateSummary bestCandidate,
        CancellationToken cancellationToken)
    {
        var spawnAck = await SpawnPlacedBrainAsync(
                new SpawnBrain
                {
                    BrainDef = bestCandidate.DefinitionArtifact.Clone(),
                    InputWidth = taskContract.InputWidth,
                    OutputWidth = taskContract.OutputWidth
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (spawnAck is null
            || !spawnAck.BrainId.TryToGuid(out var brainId)
            || brainId == Guid.Empty
            || !string.IsNullOrWhiteSpace(spawnAck.FailureReasonCode)
            || !spawnAck.PlacementReady)
        {
            return null;
        }

        _trackedBrains.TryAdd(brainId, 0);
        var snapshotArtifact = await TryRequestSnapshotArtifactAsync(brainId, cancellationToken).ConfigureAwait(false);
        _trackedBrains.TryRemove(brainId, out _);

        var retainedMember = new PopulationMember(
            bestCandidate.DefinitionArtifact.Clone(),
            bestCandidate.SpeciesId,
            bestCandidate.SpeciesId,
            bestCandidate.Complexity,
            RehydrateEvaluation(bestCandidate),
            EvaluationGeneration: bestCandidate.Generation,
            ActiveBrainId: brainId,
            SnapshotArtifact: snapshotArtifact?.Clone())
        {
            BootstrapOrigin = bestCandidate.BootstrapOrigin
        };
        var retainedSummary = new BasicsExecutionBestCandidateSummary(
            bestCandidate.DefinitionArtifact.Clone(),
            snapshotArtifact?.Clone(),
            brainId,
            bestCandidate.SpeciesId,
            bestCandidate.Accuracy,
            bestCandidate.Fitness,
            bestCandidate.Complexity,
            new Dictionary<string, float>(bestCandidate.ScoreBreakdown, StringComparer.Ordinal),
            bestCandidate.Diagnostics.ToArray(),
            bestCandidate.Generation,
            bestCandidate.AverageReadyTickCount,
            bestCandidate.MinReadyTickCount,
            bestCandidate.MedianReadyTickCount,
            bestCandidate.MaxReadyTickCount)
        {
            BootstrapOrigin = bestCandidate.BootstrapOrigin
        };
        return (retainedMember, retainedSummary);
    }

    private async Task<SpawnBrainAck?> SpawnPlacedBrainAsync(
        SpawnBrain request,
        CancellationToken cancellationToken,
        Action<InFlightBrainPhase>? onProgress = null)
    {
        var ack = await RequestBrainSpawnAsync(request, cancellationToken).ConfigureAwait(false);
        if (ack is null)
        {
            return null;
        }

        if (!ack.BrainId.TryToGuid(out var brainId) || brainId == Guid.Empty)
        {
            return ack;
        }

        if (ack.PlacementReady || !string.IsNullOrWhiteSpace(ack.FailureReasonCode))
        {
            return ack;
        }

        onProgress?.Invoke(InFlightBrainPhase.WaitingForPlacement);
        return await AwaitBrainPlacementAsync(brainId, cancellationToken).ConfigureAwait(false) ?? ack;
    }

    private async Task<SpawnBrainAck?> RequestBrainSpawnAsync(
        SpawnBrain request,
        CancellationToken cancellationToken)
    {
        var spawn = await _runtimeClient.SpawnBrainAsync(request, cancellationToken).ConfigureAwait(false);
        return spawn?.Ack;
    }

    private async Task<SpawnBrainAck?> AwaitBrainPlacementAsync(
        Guid brainId,
        CancellationToken cancellationToken)
    {
        var awaited = await _runtimeClient.AwaitSpawnPlacementAsync(
                brainId,
                _spawnPlacementTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return awaited?.Ack;
    }

    private async Task<List<PopulationMember>> TeardownPopulationBrainsAsync(
        IReadOnlyList<PopulationMember> population,
        CancellationToken cancellationToken)
    {
        var activeBrainIds = population
            .Select(static member => member.ActiveBrainId)
            .Where(static brainId => brainId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (activeBrainIds.Length > 0)
        {
            await Task.WhenAll(activeBrainIds.Select(brainId => TeardownBrainAsync(brainId, cancellationToken))).ConfigureAwait(false);
        }

        return population
            .Select(static member => member with
            {
                ActiveBrainId = Guid.Empty,
                SnapshotArtifact = null
            })
            .ToList();
    }

    private async Task<ArtifactRef?> TryRequestSnapshotArtifactAsync(Guid brainId, CancellationToken cancellationToken)
    {
        if (brainId == Guid.Empty)
        {
            return null;
        }

        var ready = await _runtimeClient.RequestSnapshotAsync(brainId, cancellationToken).ConfigureAwait(false);
        return HasArtifactRef(ready?.Snapshot) ? ready!.Snapshot.Clone() : null;
    }

    private async Task<BasicsExecutionBestCandidateSummary?> EnsureBestCandidateSnapshotAsync(
        BasicsExecutionBestCandidateSummary? previousBestCandidate,
        BasicsExecutionBestCandidateSummary? currentBestCandidate,
        CancellationToken cancellationToken)
    {
        if (!IsViableBestCandidate(currentBestCandidate))
        {
            return CloneBestCandidate(currentBestCandidate);
        }

        var sameWinner = previousBestCandidate is not null
            && HasSameDefinitionArtifact(previousBestCandidate.DefinitionArtifact, currentBestCandidate!.DefinitionArtifact);
        if (sameWinner && HasArtifactRef(previousBestCandidate!.SnapshotArtifact))
        {
            return CloneBestCandidate(currentBestCandidate! with
            {
                SnapshotArtifact = previousBestCandidate.SnapshotArtifact!.Clone()
            });
        }

        if (HasArtifactRef(currentBestCandidate!.SnapshotArtifact))
        {
            return CloneBestCandidate(currentBestCandidate);
        }

        if (currentBestCandidate.ActiveBrainId is not Guid activeBrainId || activeBrainId == Guid.Empty)
        {
            return CloneBestCandidate(currentBestCandidate);
        }

        try
        {
            var snapshotArtifact = await TryRequestSnapshotArtifactAsync(activeBrainId, cancellationToken).ConfigureAwait(false);
            return CloneBestCandidate(currentBestCandidate with
            {
                SnapshotArtifact = snapshotArtifact?.Clone()
            });
        }
        catch
        {
            return CloneBestCandidate(currentBestCandidate);
        }
    }

    private async Task CleanupTrackedBrainsAsync(CancellationToken cancellationToken)
    {
        foreach (var brainId in _trackedBrains.Keys.ToArray())
        {
            await TeardownBrainAsync(brainId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TeardownBrainAsync(Guid brainId, CancellationToken cancellationToken)
    {
        if (brainId == Guid.Empty)
        {
            return;
        }

        try
        {
            await _runtimeClient.KillBrainAsync(brainId, "basics_evaluation_complete", cancellationToken).ConfigureAwait(false);
            await WaitForBrainTerminationAsync(brainId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown only.
        }
        finally
        {
            _trackedBrains.TryRemove(brainId, out _);
        }
    }

    private async Task<List<PopulationMember>> BreedNextGenerationAsync(
        BasicsEnvironmentPlan plan,
        IReadOnlyList<PopulationMember> population,
        int targetPopulation,
        int minimumPopulation,
        int stalledGenerationCount,
        CancellationToken cancellationToken,
        Action<string, int>? onBreedProgress,
        Action<uint>? onReproductionObserved)
    {
        var ranked = population
            .OrderByDescending(member => member.LastEvaluation?.Fitness ?? 0f)
            .ThenByDescending(member => member.LastEvaluation?.Accuracy ?? 0f)
            .ToArray();
        if (ranked.Length == 0)
        {
            return new List<PopulationMember>();
        }

        var adaptiveBoostSteps = BasicsDiversityTuning.ResolveAdaptiveBoostSteps(plan.AdaptiveDiversity, stalledGenerationCount);
        var effectivePreset = BasicsDiversityTuning.ResolveEffectivePreset(plan.DiversityPreset, adaptiveBoostSteps);
        var effectiveScheduling = BasicsDiversityTuning.ApplyAdaptiveBoost(plan.Scheduling, adaptiveBoostSteps);
        var eliteCount = ranked.Length == 1
            ? 1
            : Math.Max(1, (int)Math.Ceiling(ranked.Length * effectiveScheduling.ParentSelection.EliteFraction));
        eliteCount = Math.Min(eliteCount, Math.Min(ranked.Length, targetPopulation));

        var carryForwardMembers = SelectCarryForwardMembers(ranked, eliteCount).ToArray();
        var nextGeneration = carryForwardMembers.Select(static member => member with
        {
            LastEvaluation = member.LastEvaluation,
            ActiveBrainId = Guid.Empty,
            SnapshotArtifact = null,
            CountsTowardOffspringMetrics = false
        }).ToList();
        var seenDefinitionShas = new HashSet<string>(
            nextGeneration.Select(static member => member.Definition.ToSha256Hex()),
            StringComparer.OrdinalIgnoreCase);
        var duplicateChildDefinitionCount = 0;
        var parentPool = await BuildParentPoolAsync(
                effectiveScheduling.ParentSelection,
                population,
                carryForwardMembers,
                plan,
                adaptiveBoostSteps,
                cancellationToken)
            .ConfigureAwait(false);
        if (parentPool.Count == 0)
        {
            return EnsureMinimumCarryForwardPopulation(nextGeneration, ranked, targetPopulation, minimumPopulation);
        }

        var bestFitness = Math.Max(0.0001f, ranked.Max(member => member.LastEvaluation?.Fitness ?? 0f));
        var compatibilityCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var queuedPairKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queuedPairs = new Queue<QueuedBreedingPair>();
        await EnqueueEliteCrossPairsAsync(
                carryForwardMembers,
                population,
                plan,
                adaptiveBoostSteps,
                bestFitness,
                queuedPairs,
                queuedPairKeys,
                compatibilityCache,
                cancellationToken)
            .ConfigureAwait(false);
        var frontierChildren = new List<FrontierChildCandidate>();
        var frontierCap = ResolveWithinGenerationChildFrontierCap(targetPopulation);
        var maxAttempts = Math.Max(targetPopulation * 4, 8);
        var attempt = 0;
        while (nextGeneration.Count < targetPopulation && attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            PopulationMember parentA;
            PopulationMember parentB;
            float normalizedFitness;
            float normalizedNovelty;
            string pairingReason;
            if (queuedPairs.Count > 0)
            {
                var queuedPair = queuedPairs.Dequeue();
                parentA = queuedPair.ParentA;
                parentB = queuedPair.ParentB;
                normalizedFitness = queuedPair.NormalizedFitnessHint;
                normalizedNovelty = queuedPair.NormalizedNoveltyHint;
                pairingReason = queuedPair.PairingReason;
            }
            else
            {
                parentA = parentPool[attempt % parentPool.Count];
                parentB = SelectPairParent(parentPool, parentA, attempt);
                normalizedFitness = ResolveNormalizedFitnessHint(parentA, parentB, bestFitness);
                normalizedNovelty = ResolveNormalizedNoveltyHint(parentA, parentB, population);
                pairingReason = "ranked parent pool";
            }
            var runCount = BasicsReproductionBudgetPlanner.ResolveRunCount(
                effectiveScheduling.RunAllocation,
                plan.Capacity.RecommendedReproductionRunCount,
                normalizedFitness,
                normalizedNovelty);
            onBreedProgress?.Invoke(
                adaptiveBoostSteps > 0
                    ? $"Selected parents {parentA.SpeciesId} × {parentB.SpeciesId} via {pairingReason}; requesting {runCount} child run(s). Adaptive diversity {plan.DiversityPreset} -> {effectivePreset} after {stalledGenerationCount} stalled generation(s)."
                    : $"Selected parents {parentA.SpeciesId} × {parentB.SpeciesId} via {pairingReason}; requesting {runCount} child run(s). Diversity preset {plan.DiversityPreset}.",
                nextGeneration.Count);

            var reproduceConfig = plan.Reproduction.Config.Clone();
            ApplyVariationBand(reproduceConfig, plan.SeedTemplate.InitialVariationBand);
            BasicsDiversityTuning.ApplyAdaptiveBoost(reproduceConfig, adaptiveBoostSteps);
            reproduceConfig.SpawnChild = Repro.SpawnChildPolicy.SpawnChildNever;
            var result = await _runtimeClient.ReproduceByArtifactsAsync(
                    new Repro.ReproduceByArtifactsRequest
                    {
                        ParentADef = parentA.Definition.Clone(),
                        ParentBDef = parentB.Definition.Clone(),
                        StrengthSource = plan.Reproduction.StrengthSource,
                        Config = reproduceConfig,
                        Seed = NextSeed(),
                        RunCount = runCount
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var observedRunCount = result is null || result.RequestedRunCount == 0
                ? runCount
                : result.RequestedRunCount;
            onReproductionObserved?.Invoke(observedRunCount);
            foreach (var childDefinition in ExtractChildDefinitions(result))
            {
                if (nextGeneration.Count >= targetPopulation)
                {
                    break;
                }

                if (!seenDefinitionShas.Add(childDefinition.ToSha256Hex()))
                {
                    duplicateChildDefinitionCount++;
                    continue;
                }
                var childComplexity = await ResolveDefinitionComplexityAsync(childDefinition, knownShape: null, cancellationToken).ConfigureAwait(false);
                var membership = await CommitArtifactMembershipAsync(
                        childDefinition,
                        new[]
                        {
                            CreateSpeciationParentRef(parentA.Definition, parentA.ActiveBrainId),
                            CreateSpeciationParentRef(parentB.Definition, parentB.ActiveBrainId)
                        },
                        explicitSpeciesId: null,
                        explicitSpeciesDisplayName: null,
                        decisionReason: "basics_generation_child",
                        cancellationToken)
                    .ConfigureAwait(false);
                var childMember = new PopulationMember(
                    childDefinition.Clone(),
                    membership.SpeciesId,
                    membership.SpeciesDisplayName,
                    childComplexity,
                    LastEvaluation: null,
                    CountsTowardOffspringMetrics: true);
                nextGeneration.Add(childMember);

                if (frontierCap > 0)
                {
                    var frontierChild = new FrontierChildCandidate(
                        childMember,
                        normalizedFitness,
                        normalizedNovelty);
                    frontierChildren.Add(frontierChild);
                    if (frontierChildren.Count > frontierCap)
                    {
                        frontierChildren.RemoveAt(0);
                    }

                    await EnqueueWithinGenerationFrontierPairsAsync(
                            frontierChild,
                            frontierChildren,
                            carryForwardMembers,
                            population,
                            targetPopulation,
                            plan,
                            adaptiveBoostSteps,
                            bestFitness,
                            queuedPairs,
                            queuedPairKeys,
                            compatibilityCache,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (duplicateChildDefinitionCount > 0)
        {
            onBreedProgress?.Invoke(
                $"Skipped {duplicateChildDefinitionCount} duplicate child definition(s) while assembling the next generation.",
                nextGeneration.Count);
        }

        return EnsureMinimumCarryForwardPopulation(nextGeneration, ranked, targetPopulation, minimumPopulation);
    }

    private static List<PopulationMember> EnsureMinimumCarryForwardPopulation(
        List<PopulationMember> nextGeneration,
        IReadOnlyList<PopulationMember> ranked,
        int targetPopulation,
        int minimumPopulation)
    {
        var uniqueRankedCount = ranked
            .Select(static member => member.Definition.ToSha256Hex())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var effectiveMinimumPopulation = Math.Min(Math.Max(1, minimumPopulation), Math.Min(targetPopulation, uniqueRankedCount));
        var desiredPopulation = effectiveMinimumPopulation;
        if (nextGeneration.Count >= desiredPopulation)
        {
            return nextGeneration;
        }

        var seenDefinitionShas = new HashSet<string>(
            nextGeneration.Select(static member => member.Definition.ToSha256Hex()),
            StringComparer.OrdinalIgnoreCase);
        foreach (var member in ranked)
        {
            if (nextGeneration.Count >= desiredPopulation)
            {
                break;
            }

            if (!seenDefinitionShas.Add(member.Definition.ToSha256Hex()))
            {
                continue;
            }

            nextGeneration.Add(member with
            {
                ActiveBrainId = Guid.Empty,
                SnapshotArtifact = null,
                CountsTowardOffspringMetrics = false
            });
        }

        return nextGeneration;
    }

    private async Task<List<PopulationMember>> BuildParentPoolAsync(
        BasicsParentSelectionPolicy policy,
        IReadOnlyList<PopulationMember> population,
        IReadOnlyList<PopulationMember> carryForwardMembers,
        BasicsEnvironmentPlan plan,
        int adaptiveBoostSteps,
        CancellationToken cancellationToken)
    {
        if (population.Count == 0)
        {
            return new List<PopulationMember>();
        }

        var dedupedPopulation = population
            .GroupBy(member => member.Definition.ToSha256Hex(), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(static member => member.LastEvaluation?.Fitness ?? 0f)
                .ThenByDescending(static member => member.LastEvaluation?.Accuracy ?? 0f)
                .First())
            .ToArray();

        var speciesCounts = dedupedPopulation
            .GroupBy(member => NormalizeSpeciesId(member.SpeciesId))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var maxFitness = Math.Max(0.0001f, dedupedPopulation.Max(member => member.LastEvaluation?.Fitness ?? 0f));
        var maxSpeciesCount = Math.Max(1, speciesCounts.Values.DefaultIfEmpty(1).Max());

        var ranked = dedupedPopulation
            .Select(member =>
            {
                var speciesId = NormalizeSpeciesId(member.SpeciesId);
                var speciesCount = speciesCounts.TryGetValue(speciesId, out var count) ? count : 1;
                var novelty = 1f - (speciesCount / (float)dedupedPopulation.Length);
                var speciesBalance = 1f - (speciesCount / (float)maxSpeciesCount);
                var score = BasicsReproductionBudgetPlanner.ScoreParentCandidate(
                    policy,
                    normalizedFitness: (member.LastEvaluation?.Fitness ?? 0f) / maxFitness,
                    normalizedNovelty: novelty,
                    normalizedSpeciesBalance: speciesBalance);
                return new RankedParent(member, score);
            })
            .OrderByDescending(entry => entry.Score.WeightedScore)
            .ThenByDescending(entry => entry.Member.LastEvaluation?.Accuracy ?? 0f)
            .ToArray();

        var pool = new List<PopulationMember>(ranked.Length);
        var parentsPerSpecies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ranked)
        {
            var speciesId = NormalizeSpeciesId(entry.Member.SpeciesId);
            var currentCount = parentsPerSpecies.TryGetValue(speciesId, out var count) ? count : 0;
            if (currentCount >= policy.MaxParentsPerSpecies)
            {
                continue;
            }

            parentsPerSpecies[speciesId] = currentCount + 1;
            pool.Add(entry.Member);
        }

        var explorationCount = Math.Min(
            Math.Max(0, (int)Math.Round(dedupedPopulation.Length * policy.ExplorationFraction, MidpointRounding.AwayFromZero)),
            Math.Max(0, ranked.Length - pool.Count));
        if (explorationCount > 0)
        {
            var remaining = ranked
                .Select(entry => entry.Member)
                .Where(member => !pool.Contains(member))
                .OrderBy(_ => _random.NextDouble())
                .Take(explorationCount);
            pool.AddRange(remaining);
        }

        var temporaryEliteDuplicates = await BuildTemporaryEliteDuplicatesForReproductionAsync(
                carryForwardMembers,
                plan,
                adaptiveBoostSteps,
                cancellationToken)
            .ConfigureAwait(false);
        if (temporaryEliteDuplicates.Count > 0)
        {
            pool.AddRange(temporaryEliteDuplicates);
        }

        return pool;
    }

    private async Task EnqueueEliteCrossPairsAsync(
        IReadOnlyList<PopulationMember> carryForwardMembers,
        IReadOnlyList<PopulationMember> population,
        BasicsEnvironmentPlan plan,
        int adaptiveBoostSteps,
        float bestFitness,
        Queue<QueuedBreedingPair> queuedPairs,
        HashSet<string> queuedPairKeys,
        Dictionary<string, bool> compatibilityCache,
        CancellationToken cancellationToken)
    {
        if (carryForwardMembers.Count < 2)
        {
            return;
        }

        for (var i = 0; i < carryForwardMembers.Count; i++)
        {
            for (var j = i + 1; j < carryForwardMembers.Count; j++)
            {
                var parentA = carryForwardMembers[i];
                var parentB = carryForwardMembers[j];
                await TryEnqueueBreedingPairAsync(
                        parentA,
                        parentB,
                        ResolveNormalizedFitnessHint(parentA, parentB, bestFitness),
                        ResolveNormalizedNoveltyHint(parentA, parentB, population),
                        "elite cross queue",
                        plan,
                        adaptiveBoostSteps,
                        queuedPairs,
                        queuedPairKeys,
                        compatibilityCache,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task EnqueueWithinGenerationFrontierPairsAsync(
        FrontierChildCandidate frontierChild,
        IReadOnlyList<FrontierChildCandidate> frontierChildren,
        IReadOnlyList<PopulationMember> carryForwardMembers,
        IReadOnlyList<PopulationMember> population,
        int targetPopulation,
        BasicsEnvironmentPlan plan,
        int adaptiveBoostSteps,
        float bestFitness,
        Queue<QueuedBreedingPair> queuedPairs,
        HashSet<string> queuedPairKeys,
        Dictionary<string, bool> compatibilityCache,
        CancellationToken cancellationToken)
    {
        var elitePartnerCount = ResolveWithinGenerationElitePartnerCount(targetPopulation, carryForwardMembers.Count);
        foreach (var elite in carryForwardMembers.Take(elitePartnerCount))
        {
            var normalizedFitness = Math.Clamp(
                (frontierChild.NormalizedFitnessHint + ResolveNormalizedFitnessHint(elite, bestFitness)) / 2f,
                0f,
                1f);
            var normalizedNovelty = Math.Max(
                frontierChild.NormalizedNoveltyHint,
                ComputeNovelty(elite, population));
            await TryEnqueueBreedingPairAsync(
                    frontierChild.Member,
                    elite,
                    normalizedFitness,
                    normalizedNovelty,
                    "within-generation child x elite",
                    plan,
                    adaptiveBoostSteps,
                    queuedPairs,
                    queuedPairKeys,
                    compatibilityCache,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (frontierChildren.Count < 2 || targetPopulation < 6)
        {
            return;
        }

        foreach (var sibling in frontierChildren
                     .Where(candidate => !string.Equals(
                         candidate.Member.Definition.ToSha256Hex(),
                         frontierChild.Member.Definition.ToSha256Hex(),
                         StringComparison.OrdinalIgnoreCase))
                     .Reverse()
                     .Take(targetPopulation >= 12 ? 2 : 1))
        {
            await TryEnqueueBreedingPairAsync(
                    frontierChild.Member,
                    sibling.Member,
                    Math.Clamp((frontierChild.NormalizedFitnessHint + sibling.NormalizedFitnessHint) / 2f, 0f, 1f),
                    Math.Max(frontierChild.NormalizedNoveltyHint, sibling.NormalizedNoveltyHint),
                    "within-generation child frontier",
                    plan,
                    adaptiveBoostSteps,
                    queuedPairs,
                    queuedPairKeys,
                    compatibilityCache,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task TryEnqueueBreedingPairAsync(
        PopulationMember parentA,
        PopulationMember parentB,
        float normalizedFitnessHint,
        float normalizedNoveltyHint,
        string pairingReason,
        BasicsEnvironmentPlan plan,
        int adaptiveBoostSteps,
        Queue<QueuedBreedingPair> queuedPairs,
        HashSet<string> queuedPairKeys,
        Dictionary<string, bool> compatibilityCache,
        CancellationToken cancellationToken)
    {
        if (string.Equals(parentA.Definition.ToSha256Hex(), parentB.Definition.ToSha256Hex(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var pairKey = BuildCompatibilityPairKey(parentA.Definition, parentB.Definition);
        if (!queuedPairKeys.Add(pairKey))
        {
            return;
        }

        if (!await AreDefinitionsCompatibleForReproductionAsync(
                parentA.Definition,
                parentB.Definition,
                plan,
                adaptiveBoostSteps,
                compatibilityCache,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return;
        }

        queuedPairs.Enqueue(new QueuedBreedingPair(
            parentA,
            parentB,
            Math.Clamp(normalizedFitnessHint, 0f, 1f),
            Math.Clamp(normalizedNoveltyHint, 0f, 1f),
            pairingReason));
    }

    private static float ResolveNormalizedFitnessHint(
        PopulationMember parentA,
        PopulationMember parentB,
        float bestFitness)
        => Math.Clamp(
            (((parentA.LastEvaluation?.Fitness ?? 0f) + (parentB.LastEvaluation?.Fitness ?? 0f)) / 2f) / Math.Max(0.0001f, bestFitness),
            0f,
            1f);

    private static float ResolveNormalizedFitnessHint(PopulationMember parent, float bestFitness)
        => Math.Clamp((parent.LastEvaluation?.Fitness ?? 0f) / Math.Max(0.0001f, bestFitness), 0f, 1f);

    private float ResolveNormalizedNoveltyHint(
        PopulationMember parentA,
        PopulationMember parentB,
        IReadOnlyList<PopulationMember> population)
        => Math.Max(
            ComputeNovelty(parentA, population),
            ComputeNovelty(parentB, population));

    private static int ResolveWithinGenerationChildFrontierCap(int targetPopulation)
        => Math.Clamp(targetPopulation / 3, 0, 4);

    private static int ResolveWithinGenerationElitePartnerCount(int targetPopulation, int eliteCount)
        => targetPopulation >= 6
            ? Math.Min(2, eliteCount)
            : Math.Min(1, eliteCount);

    private static IReadOnlyList<PopulationMember> SelectCarryForwardMembers(
        IReadOnlyList<PopulationMember> ranked,
        int eliteCount)
    {
        if (ranked.Count == 0 || eliteCount <= 0)
        {
            return Array.Empty<PopulationMember>();
        }

        var selected = new List<PopulationMember>(Math.Min(eliteCount, ranked.Count));
        var seenDefinitionShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in ranked)
        {
            if (!seenDefinitionShas.Add(member.Definition.ToSha256Hex()))
            {
                continue;
            }

            selected.Add(member);
            if (selected.Count >= eliteCount)
            {
                break;
            }
        }

        return selected;
    }

    private async Task<List<PopulationMember>> BuildTemporaryEliteDuplicatesForReproductionAsync(
        IReadOnlyList<PopulationMember> carryForwardMembers,
        BasicsEnvironmentPlan plan,
        int adaptiveBoostSteps,
        CancellationToken cancellationToken)
    {
        var uniqueElites = carryForwardMembers
            .GroupBy(member => member.Definition.ToSha256Hex(), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (uniqueElites.Length == 0)
        {
            return new List<PopulationMember>();
        }

        var compatibilityCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var temporaryDuplicates = new List<PopulationMember>();
        for (var i = 0; i < uniqueElites.Length; i++)
        {
            var elite = uniqueElites[i];
            var hasCompatiblePeer = false;
            for (var j = 0; j < uniqueElites.Length; j++)
            {
                if (i == j)
                {
                    continue;
                }

                if (await AreDefinitionsCompatibleForReproductionAsync(
                        elite.Definition,
                        uniqueElites[j].Definition,
                        plan,
                        adaptiveBoostSteps,
                        compatibilityCache,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    hasCompatiblePeer = true;
                    break;
                }
            }

            if (!hasCompatiblePeer)
            {
                temporaryDuplicates.Add(elite with
                {
                    ActiveBrainId = Guid.Empty,
                    SnapshotArtifact = null,
                    CountsTowardOffspringMetrics = false
                });
            }
        }

        return temporaryDuplicates;
    }

    private async Task<bool> AreDefinitionsCompatibleForReproductionAsync(
        ArtifactRef parentA,
        ArtifactRef parentB,
        BasicsEnvironmentPlan plan,
        int adaptiveBoostSteps,
        Dictionary<string, bool> compatibilityCache,
        CancellationToken cancellationToken)
    {
        if (!HasArtifactRef(parentA) || !HasArtifactRef(parentB))
        {
            return false;
        }

        var pairKey = BuildCompatibilityPairKey(parentA, parentB);
        if (compatibilityCache.TryGetValue(pairKey, out var cached))
        {
            return cached;
        }

        var reproduceConfig = plan.Reproduction.Config.Clone();
        ApplyVariationBand(reproduceConfig, plan.SeedTemplate.InitialVariationBand);
        BasicsDiversityTuning.ApplyAdaptiveBoost(reproduceConfig, adaptiveBoostSteps);
        reproduceConfig.SpawnChild = Repro.SpawnChildPolicy.SpawnChildNever;

        var result = await _runtimeClient.AssessCompatibilityByArtifactsAsync(
                new Repro.AssessCompatibilityByArtifactsRequest
                {
                    ParentADef = parentA.Clone(),
                    ParentBDef = parentB.Clone(),
                    StrengthSource = plan.Reproduction.StrengthSource,
                    Config = reproduceConfig,
                    Seed = NextSeed(),
                    RunCount = 1
                },
                cancellationToken)
            .ConfigureAwait(false);
        var compatible = result?.Report?.Compatible ?? false;
        compatibilityCache[pairKey] = compatible;
        return compatible;
    }

    private static string BuildCompatibilityPairKey(ArtifactRef left, ArtifactRef right)
    {
        var leftSha = left.ToSha256Hex();
        var rightSha = right.ToSha256Hex();
        return string.Compare(leftSha, rightSha, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{leftSha}|{rightSha}"
            : $"{rightSha}|{leftSha}";
    }

    private PopulationMember SelectPairParent(
        IReadOnlyList<PopulationMember> parentPool,
        PopulationMember parentA,
        int attempt)
    {
        foreach (var candidate in parentPool.Skip(attempt % parentPool.Count).Concat(parentPool.Take(attempt % parentPool.Count)))
        {
            if (!ReferenceEquals(candidate, parentA)
                && !string.Equals(candidate.Definition.ToSha256Hex(), parentA.Definition.ToSha256Hex(), StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(NormalizeSpeciesId(candidate.SpeciesId), NormalizeSpeciesId(parentA.SpeciesId), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return parentPool.FirstOrDefault(candidate => !ReferenceEquals(candidate, parentA)) ?? parentA;
    }

    private float ComputeNovelty(PopulationMember member, IReadOnlyList<PopulationMember> population)
    {
        if (population.Count <= 1)
        {
            return 0f;
        }

        var speciesCount = population.Count(candidate =>
            string.Equals(NormalizeSpeciesId(candidate.SpeciesId), NormalizeSpeciesId(member.SpeciesId), StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(1f - (speciesCount / (float)population.Count), 0f, 1f);
    }

    private async Task<(string SpeciesId, string SpeciesDisplayName)> CommitArtifactMembershipAsync(
        ArtifactRef candidate,
        IReadOnlyList<ProtoSpec.SpeciationParentRef> parents,
        string? explicitSpeciesId,
        string? explicitSpeciesDisplayName,
        string decisionReason,
        CancellationToken cancellationToken)
    {
        var request = new ProtoSpec.SpeciationAssignRequest
        {
            ApplyMode = ProtoSpec.SpeciationApplyMode.Commit,
            Candidate = new ProtoSpec.SpeciationCandidateRef
            {
                ArtifactRef = candidate.Clone()
            },
            SpeciesId = explicitSpeciesId ?? string.Empty,
            SpeciesDisplayName = explicitSpeciesDisplayName ?? string.Empty,
            DecisionReason = decisionReason,
            DecisionMetadataJson = "{}"
        };

        foreach (var parent in parents)
        {
            request.Parents.Add(parent.Clone());
        }

        var response = await _runtimeClient.AssignSpeciationAsync(request, cancellationToken).ConfigureAwait(false);
        var decision = response?.Decision;
        if (decision is null)
        {
            throw new InvalidOperationException("Speciation assignment returned no decision.");
        }

        if (!decision.Success && !decision.ImmutableConflict)
        {
            throw new InvalidOperationException(
                $"Speciation assignment failed: {decision.FailureReason} {decision.FailureDetail}".Trim());
        }

        var speciesId = NormalizeSpeciesId(decision?.SpeciesId);
        if (string.IsNullOrWhiteSpace(decision?.SpeciesId) && !string.IsNullOrWhiteSpace(explicitSpeciesId))
        {
            speciesId = NormalizeSpeciesId(explicitSpeciesId);
        }
        else if (string.IsNullOrWhiteSpace(decision?.SpeciesId) && parents.Count > 0)
        {
            speciesId = NormalizeSpeciesId(ResolveSpeciationParentFallbackSpeciesId(parents[0]));
        }

        var displayName = string.IsNullOrWhiteSpace(decision?.SpeciesDisplayName)
            ? (explicitSpeciesDisplayName ?? speciesId)
            : decision!.SpeciesDisplayName;
        return (speciesId, displayName);
    }

    private static ProtoSpec.SpeciationParentRef CreateSpeciationParentRef(ArtifactRef definition)
        => new()
        {
            ArtifactRef = definition.Clone()
        };

    private static ProtoSpec.SpeciationParentRef CreateSpeciationParentRef(ArtifactRef definition, Guid activeBrainId)
        => activeBrainId != Guid.Empty
            ? new ProtoSpec.SpeciationParentRef
            {
                BrainId = activeBrainId.ToProtoUuid()
            }
            : CreateSpeciationParentRef(definition);

    private static string ResolveSpeciationParentFallbackSpeciesId(ProtoSpec.SpeciationParentRef parent)
    {
        if (parent.ParentCase == ProtoSpec.SpeciationParentRef.ParentOneofCase.ArtifactRef
            && parent.ArtifactRef is not null)
        {
            return parent.ArtifactRef.ToSha256Hex();
        }

        if (parent.ParentCase == ProtoSpec.SpeciationParentRef.ParentOneofCase.BrainId
            && parent.BrainId?.TryToGuid(out var brainId) == true)
        {
            return brainId.ToString("N");
        }

        return string.Empty;
    }

    private static void ApplyVariationBand(Repro.ReproduceConfig config, BasicsSeedVariationBand variationBand)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(variationBand);

        config.Limits ??= new Repro.ReproduceLimits();
        config.Limits.MaxNeuronsAddedAbs = (uint)Math.Max(0, variationBand.MaxInternalNeuronDelta);
        config.Limits.MaxNeuronsRemovedAbs = (uint)Math.Max(0, variationBand.MaxInternalNeuronDelta);
        config.Limits.MaxAxonsAddedAbs = (uint)Math.Max(0, variationBand.MaxAxonDelta);
        config.Limits.MaxAxonsRemovedAbs = (uint)Math.Max(0, variationBand.MaxAxonDelta);
        config.Limits.MaxRegionsAddedAbs = variationBand.AllowRegionSetChange ? 1u : 0u;
        config.Limits.MaxRegionsRemovedAbs = variationBand.AllowRegionSetChange ? 1u : 0u;

        config.ProbAddAxon = variationBand.MaxAxonDelta > 0
            ? Math.Max(config.ProbAddAxon, ResolveMutationProbability(variationBand.MaxAxonDelta, 12, 0.12f))
            : 0f;
        config.ProbRemoveAxon = variationBand.MaxAxonDelta > 0
            ? Math.Max(config.ProbRemoveAxon, ResolveMutationProbability(variationBand.MaxAxonDelta, 12, 0.08f))
            : 0f;
        config.ProbRerouteAxon = variationBand.AllowAxonReroute && variationBand.MaxAxonDelta > 0
            ? Math.Max(config.ProbRerouteAxon, ResolveMutationProbability(variationBand.MaxAxonDelta, 12, 0.08f))
            : 0f;

        config.ProbDisableNeuron = variationBand.MaxInternalNeuronDelta > 0
            ? Math.Max(config.ProbDisableNeuron, ResolveMutationProbability(variationBand.MaxInternalNeuronDelta, 4, 0.08f))
            : 0f;
        config.ProbReactivateNeuron = variationBand.MaxInternalNeuronDelta > 0
            ? Math.Max(config.ProbReactivateNeuron, ResolveMutationProbability(variationBand.MaxInternalNeuronDelta, 4, 0.08f))
            : 0f;
        config.ProbAddNeuronToEmptyRegion = variationBand.AllowRegionSetChange && variationBand.MaxInternalNeuronDelta > 0
            ? Math.Max(config.ProbAddNeuronToEmptyRegion, ResolveMutationProbability(variationBand.MaxInternalNeuronDelta, 4, 0.10f))
            : 0f;
        config.ProbRemoveLastNeuronFromRegion = variationBand.AllowRegionSetChange && variationBand.MaxInternalNeuronDelta > 0
            ? Math.Max(config.ProbRemoveLastNeuronFromRegion, ResolveMutationProbability(variationBand.MaxInternalNeuronDelta, 4, 0.05f))
            : 0f;

        config.ProbMutate = variationBand.MaxParameterCodeDelta > 0
            ? Math.Max(config.ProbMutate, ResolveMutationProbability(variationBand.MaxParameterCodeDelta, 16, 0.18f))
            : config.ProbMutate;
        config.ProbMutateFunc = variationBand.AllowFunctionMutation
            ? Math.Max(config.ProbMutateFunc, ResolveMutationProbability(variationBand.MaxParameterCodeDelta, 16, 0.10f))
            : 0f;
        config.StrengthTransformEnabled = variationBand.MaxStrengthCodeDelta > 0;
        config.ProbStrengthMutate = variationBand.MaxStrengthCodeDelta > 0
            ? Math.Max(config.ProbStrengthMutate, ResolveMutationProbability(variationBand.MaxStrengthCodeDelta, 16, 0.18f))
            : 0f;
    }

    private static float ResolveMutationProbability(int delta, int maxReference, float ceiling)
    {
        if (delta <= 0)
        {
            return 0f;
        }

        var normalized = Math.Clamp(delta / (float)Math.Max(1, maxReference), 0f, 1f);
        return Math.Clamp(normalized * ceiling, 0.01f, ceiling);
    }

    private static IReadOnlyList<ArtifactRef> ExtractChildDefinitions(Repro.ReproduceResult? result)
    {
        var definitions = new List<ArtifactRef>();
        var seenDefinitionShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (result is null)
        {
            return definitions;
        }

        if (HasArtifactRef(result.ChildDef) && seenDefinitionShas.Add(result.ChildDef.ToSha256Hex()))
        {
            definitions.Add(result.ChildDef.Clone());
        }

        foreach (var run in result.Runs)
        {
            if (HasArtifactRef(run.ChildDef) && seenDefinitionShas.Add(run.ChildDef.ToSha256Hex()))
            {
                definitions.Add(run.ChildDef.Clone());
            }
        }

        return definitions;
    }

    private static bool HasArtifactRef(ArtifactRef? artifactRef)
        => artifactRef is not null && artifactRef.TryToSha256Bytes(out _);

    private static bool IsGenerationFullyFailed(IReadOnlyList<PopulationMember> population)
        => population.Count > 0
           && population.All(static member =>
               member.LastEvaluation is { Fitness: <= 0f, Accuracy: <= 0f } evaluation
               && evaluation.Diagnostics.Count > 0);

    private string ResolveBackingStoreRoot(string templateId)
    {
        if (!string.IsNullOrWhiteSpace(_publishingOptions.BackingStoreRoot))
        {
            Directory.CreateDirectory(_publishingOptions.BackingStoreRoot);
            return _publishingOptions.BackingStoreRoot;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "nbn-basics-artifacts",
            SanitizePathSegment(templateId));
        Directory.CreateDirectory(root);
        return root;
    }

    private async Task<BasicsDefinitionComplexitySummary?> ResolveDefinitionComplexityAsync(
        ArtifactRef definition,
        BasicsResolvedSeedShape? knownShape,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (knownShape is not null)
        {
            return BuildComplexityFromSeedShape(knownShape);
        }

        var sha = definition.ToSha256Hex();
        if (_definitionComplexityCache.TryGetValue(sha, out var cached))
        {
            return cached;
        }

        var complexity = await BasicsArtifactStoreReader.TryReadDefinitionComplexityAsync(
                definition,
                ResolveArtifactFallbackRoot(),
                cancellationToken)
            .ConfigureAwait(false);
        _definitionComplexityCache[sha] = complexity;
        return complexity;
    }

    private string ResolveArtifactFallbackRoot()
        => string.IsNullOrWhiteSpace(_publishingOptions.BackingStoreRoot)
            ? ArtifactStoreResolverOptions.ResolveDefaultArtifactRootPath()
            : _publishingOptions.BackingStoreRoot!;

    private static BasicsDefinitionComplexitySummary BuildComplexityFromSeedShape(BasicsResolvedSeedShape shape)
        => new(
            ActiveInternalRegionCount: shape.ActiveInternalRegionCount,
            InternalNeuronCount: shape.InternalNeuronCount,
            AxonCount: shape.AxonCount);

    private static int ResolveMinimumRequiredInitialPopulation(IReadOnlyList<BasicsInitialBrainSeed> initialBrainSeeds)
        => initialBrainSeeds.Count == 0
            ? MinimumPopulationSize
            : initialBrainSeeds.Sum(static seed => seed.DuplicateForReproduction ? 2 : 1);

    private static int[] DistributeAcrossTemplates(int total, int templateCount)
    {
        if (templateCount <= 0 || total <= 0)
        {
            return new int[Math.Max(0, templateCount)];
        }

        var distribution = new int[templateCount];
        for (var index = 0; index < total; index++)
        {
            distribution[index % templateCount]++;
        }

        return distribution;
    }

    private async Task<bool> IsAcceptableInitialSeedAsync(
        ArtifactRef definition,
        BasicsDefinitionComplexitySummary? complexity,
        BasicsSeedShapeConstraints bounds,
        CancellationToken cancellationToken)
    {
        var bytes = await BasicsArtifactStoreReader.ReadArtifactBytesAsync(
                definition,
                ResolveArtifactFallbackRoot(),
                cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null)
        {
            return false;
        }

        var analysis = BasicsDefinitionAnalyzer.Analyze(bytes);
        var resolvedComplexity = complexity ?? analysis.Complexity;
        return analysis.Geometry.IsValid
               && analysis.HasInputToOutputPath
               && BasicsDefinitionAnalyzer.FitsSeedShapeBounds(resolvedComplexity, bounds);
    }

    private static string ShortSeedHash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..12];

    private static string ResolveSeedContentHash(BasicsInitialBrainSeed seed)
        => string.IsNullOrWhiteSpace(seed.ContentHash)
            ? Convert.ToHexString(SHA256.HashData(seed.DefinitionBytes)).ToLowerInvariant()
            : seed.ContentHash.Trim().ToLowerInvariant();

    private static PopulationMember CreateExactBootstrapPopulationMember(
        ResolvedInitialSeedTemplate template,
        int exactCopyOrdinal)
        => new(
            template.Definition.Clone(),
            template.SpeciesId,
            template.SpeciesDisplayName,
            template.Complexity,
            LastEvaluation: null,
            SnapshotArtifact: template.SnapshotArtifact?.Clone())
        {
            BootstrapOrigin = CreateBootstrapOrigin(template, exactCopyOrdinal)
        };

    private static BasicsBootstrapOrigin CreateBootstrapOrigin(
        ResolvedInitialSeedTemplate template,
        int? exactCopyOrdinal)
        => new(
            exactCopyOrdinal.HasValue ? template.ExactCopyOriginKind : template.VariationOriginKind,
            template.SourceDisplayName,
            template.SourceContentHash,
            template.BootstrapTemplateIndex,
            exactCopyOrdinal);

    private ulong NextSeed()
    {
        var buffer = new byte[8];
        _random.NextBytes(buffer);
        return BitConverter.ToUInt64(buffer, 0);
    }

    private static string BuildBootstrapSpeciesId(string templateId)
        => $"basics.{SanitizePathSegment(templateId).ToLowerInvariant()}.bootstrap";

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "template";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Trim()
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
            .ToArray();
        return new string(chars);
    }

    private static BasicsTaskEvaluationResult CreateTransportFailure(string diagnostic)
        => new(
            Fitness: 0f,
            Accuracy: 0f,
            SamplesEvaluated: 0,
            SamplesCorrect: 0,
            ScoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["task_accuracy"] = 0f,
                ["classification_accuracy"] = 0f,
                ["tolerance_accuracy"] = 0f,
                ["edge_tolerance_accuracy"] = 0f,
                ["interior_tolerance_accuracy"] = 0f,
                ["balanced_tolerance_accuracy"] = 0f,
                ["mean_absolute_error"] = 1f,
                ["mean_squared_error"] = 1f,
                ["target_proximity_fitness"] = 0f,
                ["dataset_coverage"] = 0f,
                ["negative_mean_output"] = 1f,
                ["positive_mean_gap"] = 1f,
                ["zero_product_mean_output"] = 1f,
                ["unit_product_gap"] = 1f,
                ["midrange_mean_absolute_error"] = 1f,
                ["truth_table_coverage"] = 0f,
                ["comparison_set_coverage"] = 0f,
                ["evaluation_set_coverage"] = 0f
            },
            Diagnostics: new[] { diagnostic });

    private static string TrimDiagnosticDetail(string? failureCode, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = string.Join(" ", value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        if (string.Equals(failureCode, "spawn_internal_error", StringComparison.Ordinal)
            || string.Equals(failureCode, "spawn_wait_timeout", StringComparison.Ordinal))
        {
            const int limit = 256;
            if (collapsed.Length <= limit)
            {
                return collapsed;
            }

            const int head = 160;
            const int tail = 92;
            return $"{collapsed[..head]} ... {collapsed[^tail..]}";
        }

        return collapsed.Length <= 96 ? collapsed : collapsed[..96];
    }

    private static InFlightBrainProgress CreateInFlightBrainProgress(
        PopulationMember member,
        int batchBrainOrdinal,
        int batchBrainCount,
        int attemptNumber,
        InFlightBrainPhase phase)
        => new(
            batchBrainOrdinal,
            batchBrainCount,
            attemptNumber,
            MaxMemberEvaluationAttempts,
            ShortArtifactSha(member.Definition),
            phase);

    private static string ShortArtifactSha(ArtifactRef artifact)
    {
        var sha = artifact.ToSha256Hex();
        return sha.Length <= 8 ? sha : sha[..8];
    }

    private BasicsExecutionSnapshot CreateFinalSnapshot(
        BasicsExecutionStopCriteria stopCriteria,
        Action<BasicsExecutionSnapshot> onSnapshot,
        BasicsExecutionState state,
        string statusText,
        string detailText,
        ulong? speciationEpochId,
        int generation,
        IReadOnlyList<PopulationMember> population,
        int activeBrainCount,
        ulong reproductionCalls,
        ulong reproductionRunsObserved,
        ArtifactRef? effectiveTemplateDefinition,
        BasicsResolvedSeedShape? seedShape,
        IReadOnlyList<float> accuracyHistory,
        IReadOnlyList<float> fitnessHistory,
        BasicsExecutionSnapshot? baselineSnapshot,
        float overallBestAccuracy,
        float overallBestFitness,
        BasicsExecutionBestCandidateSummary? overallBestCandidate,
        BasicsExecutionBatchTimingSummary? latestBatchTiming = null,
        BasicsExecutionGenerationTimingSummary? latestGenerationTiming = null,
        IReadOnlyList<float>? offspringAccuracyHistory = null,
        IReadOnlyList<float>? offspringBalancedAccuracyHistory = null,
        IReadOnlyList<float>? balancedAccuracyHistory = null,
        IReadOnlyList<float>? offspringEdgeAccuracyHistory = null,
        IReadOnlyList<float>? offspringInteriorAccuracyHistory = null,
        IReadOnlyList<float>? offspringFitnessHistory = null)
    {
        var snapshot = CreateTerminalSnapshot(
            stopCriteria,
            state,
            statusText,
            detailText,
            speciationEpochId,
            generation,
            population,
            activeBrainCount,
            reproductionCalls,
            reproductionRunsObserved,
            effectiveTemplateDefinition,
            seedShape,
            accuracyHistory,
            fitnessHistory,
            baselineSnapshot,
            overallBestAccuracy,
            overallBestFitness,
            overallBestCandidate,
            latestBatchTiming,
            latestGenerationTiming,
            offspringAccuracyHistory,
            offspringBalancedAccuracyHistory,
            balancedAccuracyHistory,
            offspringEdgeAccuracyHistory,
            offspringInteriorAccuracyHistory,
            offspringFitnessHistory);
        onSnapshot(snapshot);
        return snapshot;
    }

    private BasicsExecutionSnapshot CreateTerminalSnapshot(
        BasicsExecutionStopCriteria stopCriteria,
        BasicsExecutionState state,
        string statusText,
        string detailText,
        ulong? speciationEpochId,
        int generation,
        IReadOnlyList<PopulationMember> population,
        int activeBrainCount,
        ulong reproductionCalls,
        ulong reproductionRunsObserved,
        ArtifactRef? effectiveTemplateDefinition,
        BasicsResolvedSeedShape? seedShape,
        IReadOnlyList<float> accuracyHistory,
        IReadOnlyList<float> fitnessHistory,
        BasicsExecutionSnapshot? baselineSnapshot,
        float overallBestAccuracy,
        float overallBestFitness,
        BasicsExecutionBestCandidateSummary? overallBestCandidate,
        BasicsExecutionBatchTimingSummary? latestBatchTiming,
        BasicsExecutionGenerationTimingSummary? latestGenerationTiming,
        IReadOnlyList<float>? offspringAccuracyHistory = null,
        IReadOnlyList<float>? offspringBalancedAccuracyHistory = null,
        IReadOnlyList<float>? balancedAccuracyHistory = null,
        IReadOnlyList<float>? offspringEdgeAccuracyHistory = null,
        IReadOnlyList<float>? offspringInteriorAccuracyHistory = null,
        IReadOnlyList<float>? offspringFitnessHistory = null)
    {
        if (!ShouldUseBaselineSnapshot(population, baselineSnapshot))
        {
            var currentSnapshot = CreateSnapshot(
                stopCriteria,
                state,
                statusText,
                detailText,
                speciationEpochId,
                generation,
                population,
                activeBrainCount,
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory,
                includeWinnerRuntimeState: activeBrainCount > 0,
                overallBestAccuracy: overallBestAccuracy,
                overallBestFitness: overallBestFitness,
                overallBestCandidate: overallBestCandidate,
                latestBatchTiming: latestBatchTiming,
                latestGenerationTiming: latestGenerationTiming,
                offspringAccuracyHistory: offspringAccuracyHistory,
                offspringBalancedAccuracyHistory: offspringBalancedAccuracyHistory,
                balancedAccuracyHistory: balancedAccuracyHistory,
                offspringEdgeAccuracyHistory: offspringEdgeAccuracyHistory,
                offspringInteriorAccuracyHistory: offspringInteriorAccuracyHistory,
                offspringFitnessHistory: offspringFitnessHistory);

            if (!ShouldPreserveBaselinePopulationSummary(state, population, baselineSnapshot))
            {
                return currentSnapshot;
            }

            var baselineForPopulation = baselineSnapshot!;
            return currentSnapshot with
            {
                PopulationCount = Math.Max(currentSnapshot.PopulationCount, baselineForPopulation.PopulationCount),
                SpeciesCount = Math.Max(currentSnapshot.SpeciesCount, baselineForPopulation.SpeciesCount),
                MeanFitness = Math.Max(currentSnapshot.MeanFitness, baselineForPopulation.MeanFitness)
            };
        }

        var baseline = baselineSnapshot!;
        var resolvedBestAccuracy = Math.Max(Math.Max(overallBestAccuracy, baseline.BestAccuracy), accuracyHistory.DefaultIfEmpty(0f).Max());
        var resolvedBestFitness = Math.Max(Math.Max(overallBestFitness, baseline.BestFitness), fitnessHistory.DefaultIfEmpty(0f).Max());
        var resolvedBestCandidate = SelectBestCandidateSummary(overallBestCandidate, baseline.BestCandidate);
        return new BasicsExecutionSnapshot(
            State: state,
            StatusText: statusText,
            DetailText: detailText,
            SpeciationEpochId: speciationEpochId ?? baseline.SpeciationEpochId,
            EvaluationFailureCount: baseline.EvaluationFailureCount,
            EvaluationFailureSummary: baseline.EvaluationFailureSummary,
            Generation: Math.Max(generation, baseline.Generation),
            PopulationCount: baseline.PopulationCount,
            ActiveBrainCount: activeBrainCount,
            SpeciesCount: baseline.SpeciesCount,
            ReproductionCalls: reproductionCalls,
            ReproductionRunsObserved: reproductionRunsObserved,
            CapacityUtilization: 0f,
            OffspringBestAccuracy: offspringAccuracyHistory?.DefaultIfEmpty(0f).LastOrDefault() ?? 0f,
            BestAccuracy: resolvedBestAccuracy,
            OffspringBestFitness: offspringFitnessHistory?.DefaultIfEmpty(0f).LastOrDefault() ?? 0f,
            BestFitness: resolvedBestFitness,
            MeanFitness: baseline.MeanFitness,
            EffectiveTemplateDefinition: effectiveTemplateDefinition?.Clone() ?? baseline.EffectiveTemplateDefinition?.Clone(),
            SeedShape: seedShape ?? baseline.SeedShape,
            BestCandidate: CloneBestCandidate(resolvedBestCandidate),
            OffspringAccuracyHistory: offspringAccuracyHistory?.Count > 0 ? offspringAccuracyHistory.ToArray() : baseline.OffspringAccuracyHistory,
            AccuracyHistory: MergeHistory(accuracyHistory, baseline.AccuracyHistory),
            OffspringBalancedAccuracyHistory: offspringBalancedAccuracyHistory?.Count > 0 ? offspringBalancedAccuracyHistory.ToArray() : baseline.OffspringBalancedAccuracyHistory,
            BalancedAccuracyHistory: balancedAccuracyHistory?.Count > 0 ? balancedAccuracyHistory.ToArray() : baseline.BalancedAccuracyHistory,
            OffspringEdgeAccuracyHistory: offspringEdgeAccuracyHistory?.Count > 0 ? offspringEdgeAccuracyHistory.ToArray() : baseline.OffspringEdgeAccuracyHistory,
            OffspringInteriorAccuracyHistory: offspringInteriorAccuracyHistory?.Count > 0 ? offspringInteriorAccuracyHistory.ToArray() : baseline.OffspringInteriorAccuracyHistory,
            OffspringFitnessHistory: offspringFitnessHistory?.Count > 0 ? offspringFitnessHistory.ToArray() : baseline.OffspringFitnessHistory,
            BestFitnessHistory: MergeHistory(fitnessHistory, baseline.BestFitnessHistory),
            LatestBatchTiming: latestBatchTiming ?? baseline.LatestBatchTiming,
            LatestGenerationTiming: latestGenerationTiming ?? baseline.LatestGenerationTiming)
        {
            BootstrapCandidateTraces = baseline.BootstrapCandidateTraces.ToArray()
        };
    }

    private static void PublishSnapshot(
        Action<BasicsExecutionSnapshot> onSnapshot,
        BasicsExecutionState state,
        string statusText,
        string detailText,
        ulong? speciationEpochId,
        int evaluationFailureCount,
        string evaluationFailureSummary,
        int generation,
        int populationCount,
        int activeBrainCount,
        int speciesCount,
        ulong reproductionCalls,
        ulong reproductionRunsObserved,
        float capacityUtilization,
        float bestAccuracy,
        float bestFitness,
        float meanFitness,
        ArtifactRef? effectiveTemplateDefinition,
        BasicsResolvedSeedShape? seedShape,
        BasicsExecutionBestCandidateSummary? bestCandidate,
        IReadOnlyList<float> accuracyHistory,
        IReadOnlyList<float> fitnessHistory,
        float overallBestAccuracy,
        float overallBestFitness,
        BasicsExecutionBestCandidateSummary? overallBestCandidate,
        BasicsExecutionBatchTimingSummary? latestBatchTiming = null,
        BasicsExecutionGenerationTimingSummary? latestGenerationTiming = null,
        IReadOnlyList<float>? offspringAccuracyHistory = null,
        IReadOnlyList<float>? offspringBalancedAccuracyHistory = null,
        IReadOnlyList<float>? balancedAccuracyHistory = null,
        IReadOnlyList<float>? offspringEdgeAccuracyHistory = null,
        IReadOnlyList<float>? offspringInteriorAccuracyHistory = null,
        IReadOnlyList<float>? offspringFitnessHistory = null)
    {
        onSnapshot(new BasicsExecutionSnapshot(
            state,
            statusText,
            detailText,
            speciationEpochId,
            evaluationFailureCount,
            evaluationFailureSummary,
            generation,
            populationCount,
            activeBrainCount,
            speciesCount,
            reproductionCalls,
            reproductionRunsObserved,
            capacityUtilization,
            offspringAccuracyHistory?.DefaultIfEmpty(0f).LastOrDefault() ?? 0f,
            Math.Max(bestAccuracy, overallBestAccuracy),
            offspringFitnessHistory?.DefaultIfEmpty(0f).LastOrDefault() ?? 0f,
            Math.Max(bestFitness, overallBestFitness),
            meanFitness,
            effectiveTemplateDefinition,
            seedShape,
            CloneBestCandidate(SelectBestCandidateSummary(bestCandidate, overallBestCandidate)),
            offspringAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            accuracyHistory.ToArray(),
            offspringBalancedAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            balancedAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            offspringEdgeAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            offspringInteriorAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            offspringFitnessHistory?.ToArray() ?? Array.Empty<float>(),
            fitnessHistory.ToArray(),
            latestBatchTiming,
            latestGenerationTiming));
    }

    private static bool ShouldUseBaselineSnapshot(
        IReadOnlyList<PopulationMember> population,
        BasicsExecutionSnapshot? baselineSnapshot)
        => baselineSnapshot is not null
           && (population.Count == 0 || population.All(static member => member.LastEvaluation is null));

    private static bool ShouldPreserveBaselinePopulationSummary(
        BasicsExecutionState state,
        IReadOnlyList<PopulationMember> population,
        BasicsExecutionSnapshot? baselineSnapshot)
    {
        if (baselineSnapshot is null
            || state is not (BasicsExecutionState.Failed or BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded)
            || population.Count == 0
            || population.Count >= baselineSnapshot.PopulationCount)
        {
            return false;
        }

        if (population.Count != 1)
        {
            return false;
        }

        var evaluation = population[0].LastEvaluation;
        return evaluation is not null && evaluation.Diagnostics.Count == 0;
    }

    private static BasicsExecutionBestCandidateSummary? CloneBestCandidate(BasicsExecutionBestCandidateSummary? candidate)
        => candidate is null
            ? null
            : new BasicsExecutionBestCandidateSummary(
                candidate.DefinitionArtifact.Clone(),
                candidate.SnapshotArtifact?.Clone(),
                candidate.ActiveBrainId,
                candidate.SpeciesId,
                candidate.Accuracy,
                candidate.Fitness,
                candidate.Complexity,
                new Dictionary<string, float>(candidate.ScoreBreakdown, StringComparer.Ordinal),
                candidate.Diagnostics.ToArray(),
                candidate.Generation,
                candidate.AverageReadyTickCount,
                candidate.MinReadyTickCount,
                candidate.MedianReadyTickCount,
                candidate.MaxReadyTickCount,
                candidate.ReadyTickStdDev)
            {
                BootstrapOrigin = candidate.BootstrapOrigin
            };

    private static IReadOnlyList<float> MergeHistory(
        IReadOnlyList<float> current,
        IReadOnlyList<float> baseline)
        => current.Count > 0 ? current.ToArray() : baseline.ToArray();

    private static void UpdateBestSoFar(
        GenerationMetrics generationMetrics,
        ref float bestAccuracySoFar,
        ref float bestFitnessSoFar,
        ref BasicsExecutionBestCandidateSummary? bestCandidateSoFar)
    {
        bestAccuracySoFar = Math.Max(bestAccuracySoFar, generationMetrics.BestAccuracy);
        bestFitnessSoFar = Math.Max(bestFitnessSoFar, generationMetrics.BestFitness);
        if (generationMetrics.TrackedBestCandidate is null)
        {
            return;
        }

        if (!IsViableBestCandidate(generationMetrics.TrackedBestCandidate))
        {
            return;
        }

        bestCandidateSoFar = SelectBestCandidateSummary(bestCandidateSoFar, generationMetrics.TrackedBestCandidate);
    }

    private static bool DidGenerationImprove(
        GenerationMetrics generationMetrics,
        float bestAccuracySoFar,
        float bestFitnessSoFar,
        BasicsExecutionBestCandidateSummary? bestCandidateSoFar)
    {
        if (generationMetrics.BestAccuracy > bestAccuracySoFar + 0.0001f
            || generationMetrics.BestFitness > bestFitnessSoFar + 0.0001f)
        {
            return true;
        }

        return generationMetrics.TrackedBestCandidate is not null
               && (bestCandidateSoFar is null
                   || CompareCandidateSummary(generationMetrics.TrackedBestCandidate, bestCandidateSoFar) > 0);
    }

    private static BasicsExecutionBestCandidateSummary? SelectBestCandidateSummary(
        BasicsExecutionBestCandidateSummary? left,
        BasicsExecutionBestCandidateSummary? right)
    {
        if (left is null)
        {
            return CloneBestCandidate(right);
        }

        if (right is null)
        {
            return CloneBestCandidate(left);
        }

        return CompareCandidateSummary(right, left) > 0
            ? CloneBestCandidate(right)
            : CloneBestCandidate(left);
    }

    private static int CompareCandidateSummary(
        BasicsExecutionBestCandidateSummary candidate,
        BasicsExecutionBestCandidateSummary baseline)
    {
        var viabilityComparison = IsViableBestCandidate(candidate).CompareTo(IsViableBestCandidate(baseline));
        if (viabilityComparison != 0)
        {
            return viabilityComparison;
        }

        var selectionAccuracyComparison = ResolveCandidateSelectionAccuracy(candidate)
            .CompareTo(ResolveCandidateSelectionAccuracy(baseline));
        if (selectionAccuracyComparison != 0)
        {
            return selectionAccuracyComparison;
        }

        var fitnessComparison = candidate.Fitness.CompareTo(baseline.Fitness);
        if (fitnessComparison != 0)
        {
            return fitnessComparison;
        }

        var accuracyComparison = candidate.Accuracy.CompareTo(baseline.Accuracy);
        if (accuracyComparison != 0)
        {
            return accuracyComparison;
        }

        var internalNeuronComparison = CompareAscendingPreference(
            candidate.Complexity?.InternalNeuronCount,
            baseline.Complexity?.InternalNeuronCount);
        if (internalNeuronComparison != 0)
        {
            return internalNeuronComparison;
        }

        var axonComparison = CompareAscendingPreference(
            candidate.Complexity?.AxonCount,
            baseline.Complexity?.AxonCount);
        if (axonComparison != 0)
        {
            return axonComparison;
        }

        var regionComparison = CompareAscendingPreference(
            candidate.Complexity?.ActiveInternalRegionCount,
            baseline.Complexity?.ActiveInternalRegionCount);
        if (regionComparison != 0)
        {
            return regionComparison;
        }

        var snapshotComparison = candidate.HasSnapshotArtifact.CompareTo(baseline.HasSnapshotArtifact);
        if (snapshotComparison != 0)
        {
            return snapshotComparison;
        }

        var retainedBrainComparison = candidate.HasRetainedBrain.CompareTo(baseline.HasRetainedBrain);
        if (retainedBrainComparison != 0)
        {
            return retainedBrainComparison;
        }

        return ComputeTieBreakRank(baseline.ArtifactSha256).CompareTo(ComputeTieBreakRank(candidate.ArtifactSha256));
    }

    private static int CompareAscendingPreference(int? candidate, int? baseline)
    {
        var candidateValue = candidate ?? int.MaxValue;
        var baselineValue = baseline ?? int.MaxValue;
        return baselineValue.CompareTo(candidateValue);
    }

    private static bool IsViableBestCandidate(BasicsExecutionBestCandidateSummary? candidate)
        => candidate is not null && candidate.Diagnostics.Count == 0;

    private static bool HasSameDefinitionArtifact(ArtifactRef left, ArtifactRef right)
        => string.Equals(left.ToSha256Hex(), right.ToSha256Hex(), StringComparison.OrdinalIgnoreCase);

    private static BasicsTaskEvaluationResult RehydrateEvaluation(BasicsExecutionBestCandidateSummary bestCandidate)
        => new(
            Fitness: bestCandidate.Fitness,
            Accuracy: bestCandidate.Accuracy,
            SamplesEvaluated: 0,
            SamplesCorrect: 0,
            ScoreBreakdown: new Dictionary<string, float>(bestCandidate.ScoreBreakdown, StringComparer.Ordinal),
            Diagnostics: bestCandidate.Diagnostics.ToArray(),
            AverageReadyTickCount: bestCandidate.AverageReadyTickCount,
            MinReadyTickCount: bestCandidate.MinReadyTickCount,
            MedianReadyTickCount: bestCandidate.MedianReadyTickCount,
            MaxReadyTickCount: bestCandidate.MaxReadyTickCount,
            ReadyTickStdDev: bestCandidate.ReadyTickStdDev);

    private BasicsExecutionSnapshot CreateSnapshot(
        BasicsExecutionStopCriteria stopCriteria,
        BasicsExecutionState state,
        string statusText,
        string detailText,
        ulong? speciationEpochId,
        int generation,
        IReadOnlyList<PopulationMember> population,
        int activeBrainCount,
        ulong reproductionCalls,
        ulong reproductionRunsObserved,
        ArtifactRef? effectiveTemplateDefinition,
        BasicsResolvedSeedShape? seedShape,
        IReadOnlyList<float> accuracyHistory,
        IReadOnlyList<float> fitnessHistory,
        IReadOnlyList<float>? offspringAccuracyHistory = null,
        IReadOnlyList<float>? offspringBalancedAccuracyHistory = null,
        IReadOnlyList<float>? balancedAccuracyHistory = null,
        IReadOnlyList<float>? offspringEdgeAccuracyHistory = null,
        IReadOnlyList<float>? offspringInteriorAccuracyHistory = null,
        IReadOnlyList<float>? offspringFitnessHistory = null,
        bool includeWinnerRuntimeState = false,
        float overallBestAccuracy = 0f,
        float overallBestFitness = 0f,
        BasicsExecutionBestCandidateSummary? overallBestCandidate = null,
        BasicsExecutionBatchTimingSummary? latestBatchTiming = null,
        BasicsExecutionGenerationTimingSummary? latestGenerationTiming = null)
    {
        var metrics = BuildGenerationMetrics(population, stopCriteria, includeWinnerRuntimeState, generation);
        var resolvedBestAccuracy = Math.Max(metrics.BestAccuracy, Math.Max(overallBestAccuracy, accuracyHistory.DefaultIfEmpty(0f).Max()));
        var resolvedBestFitness = Math.Max(metrics.BestFitness, Math.Max(overallBestFitness, fitnessHistory.DefaultIfEmpty(0f).Max()));
        var resolvedBestCandidate = SelectBestCandidateSummary(metrics.TrackedBestCandidate, overallBestCandidate);
        return new BasicsExecutionSnapshot(
            state,
            statusText,
            detailText,
            speciationEpochId,
            metrics.EvaluationFailureCount,
            metrics.EvaluationFailureSummary,
            generation,
            population.Count,
            activeBrainCount,
            metrics.SpeciesCount,
            reproductionCalls,
            reproductionRunsObserved,
            activeBrainCount <= 0 ? metrics.CapacityUtilization : Math.Clamp(activeBrainCount / (float)Math.Max(1, population.Count), 0f, 1f),
            metrics.OffspringBestAccuracy,
            resolvedBestAccuracy,
            metrics.OffspringBestFitness,
            resolvedBestFitness,
            (float)metrics.MeanFitness,
            effectiveTemplateDefinition?.Clone(),
            seedShape,
            CloneBestCandidate(resolvedBestCandidate),
            offspringAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            accuracyHistory.ToArray(),
            offspringBalancedAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            balancedAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            offspringEdgeAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            offspringInteriorAccuracyHistory?.ToArray() ?? Array.Empty<float>(),
            offspringFitnessHistory?.ToArray() ?? Array.Empty<float>(),
            fitnessHistory.ToArray(),
            latestBatchTiming,
            latestGenerationTiming)
        {
            BootstrapCandidateTraces = CloneBootstrapCandidateTraces(metrics.BootstrapCandidateTraces)
        };
    }

    private GenerationMetrics BuildGenerationMetrics(
        IReadOnlyList<PopulationMember> population,
        BasicsExecutionStopCriteria? stopCriteria,
        bool includeWinnerRuntimeState = false,
        int? currentGeneration = null)
    {
        if (population.Count == 0)
        {
            return new GenerationMetrics(
                OffspringBestAccuracy: 0f,
                BestAccuracy: 0f,
                OffspringBestBalancedAccuracy: 0f,
                BestBalancedAccuracy: 0f,
                OffspringBestEdgeAccuracy: 0f,
                OffspringBestInteriorAccuracy: 0f,
                OffspringBestFitness: 0f,
                BestFitness: 0f,
                OffspringEvaluatedCount: 0,
                RetainedEvaluationCount: 0,
                MeanFitness: 0f,
                SpeciesCount: 0,
                CapacityUtilization: 0f,
                BestCandidate: null,
                TrackedBestCandidate: null,
                EvaluationFailureCount: 0,
                EvaluationFailureSummary: string.Empty,
                BootstrapCandidateTraces: Array.Empty<BasicsExecutionBootstrapCandidateTrace>());
        }

        var candidates = population
            .Select(member => new CandidateSelection(
                Member: member,
                Evaluation: member.LastEvaluation ?? CreateTransportFailure("evaluation_missing"),
                TieBreakRank: ComputeTieBreakRank(member.Definition.ToSha256Hex())))
            .ToArray();
        var offspringCandidates = currentGeneration.HasValue
            ? candidates.Where(candidate =>
                    candidate.Member.EvaluationGeneration == currentGeneration.Value
                    && candidate.Member.CountsTowardOffspringMetrics)
                .ToArray()
            : Array.Empty<CandidateSelection>();
        var retainedEvaluationCount = currentGeneration.HasValue
            ? population.Count(member =>
                member.LastEvaluation is not null
                && member.EvaluationGeneration.HasValue
                && member.EvaluationGeneration.Value < currentGeneration.Value)
            : 0;
        var winningCandidate = SelectWinningCandidate(candidates, stopCriteria);
        var trackedBestCandidate = SelectTrackedBestCandidate(candidates);
        var speciesCount = population.Select(member => NormalizeSpeciesId(member.SpeciesId)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var failureDiagnostics = population
            .SelectMany(member => member.LastEvaluation?.Diagnostics ?? Array.Empty<string>())
            .Where(static diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(static diagnostic => diagnostic.Trim())
            .ToArray();
        var failureCount = population.Count(member => (member.LastEvaluation?.Diagnostics.Count ?? 0) > 0);
        var failureSummary = string.Empty;
        if (failureDiagnostics.Length > 0)
        {
            var grouped = failureDiagnostics
                .GroupBy(static diagnostic => diagnostic, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(3)
                .Select(group => $"{group.Key} x{group.Count()}");
            failureSummary = string.Join("; ", grouped);
        }

        var offspringBestBalancedAccuracy = offspringCandidates.Length == 0
            ? 0f
            : offspringCandidates.Max(candidate => ResolveAccuracyBreakdownMetric(candidate.Evaluation, "balanced_tolerance_accuracy"));
        var bestBalancedAccuracy = candidates.Max(candidate => ResolveAccuracyBreakdownMetric(candidate.Evaluation, "balanced_tolerance_accuracy"));
        var offspringBestEdgeAccuracy = offspringCandidates.Length == 0
            ? 0f
            : offspringCandidates.Max(candidate => ResolveAccuracyBreakdownMetric(candidate.Evaluation, "edge_tolerance_accuracy"));
        var offspringBestInteriorAccuracy = offspringCandidates.Length == 0
            ? 0f
            : offspringCandidates.Max(candidate => ResolveAccuracyBreakdownMetric(candidate.Evaluation, "interior_tolerance_accuracy"));
        var bootstrapCandidateTraces = BuildBootstrapCandidateTraces(population, currentGeneration);

        return new GenerationMetrics(
            OffspringBestAccuracy: offspringCandidates.Length == 0
                ? 0f
                : offspringCandidates.Max(candidate => candidate.Evaluation.Accuracy),
            BestAccuracy: candidates.Max(candidate => candidate.Evaluation.Accuracy),
            OffspringBestBalancedAccuracy: offspringBestBalancedAccuracy,
            BestBalancedAccuracy: bestBalancedAccuracy,
            OffspringBestEdgeAccuracy: offspringBestEdgeAccuracy,
            OffspringBestInteriorAccuracy: offspringBestInteriorAccuracy,
            OffspringBestFitness: offspringCandidates.Length == 0
                ? 0f
                : offspringCandidates.Max(candidate => candidate.Evaluation.Fitness),
            BestFitness: candidates.Max(candidate => candidate.Evaluation.Fitness),
            OffspringEvaluatedCount: offspringCandidates.Length,
            RetainedEvaluationCount: retainedEvaluationCount,
            MeanFitness: candidates.Average(candidate => candidate.Evaluation.Fitness),
            SpeciesCount: speciesCount,
            CapacityUtilization: 0f,
            BestCandidate: BuildBestCandidateSummary(winningCandidate, includeWinnerRuntimeState),
            TrackedBestCandidate: BuildBestCandidateSummary(trackedBestCandidate, includeWinnerRuntimeState),
            EvaluationFailureCount: failureCount,
            EvaluationFailureSummary: failureSummary,
            BootstrapCandidateTraces: bootstrapCandidateTraces);
    }

    private static float ResolveAccuracyBreakdownMetric(BasicsTaskEvaluationResult evaluation, string key)
        => evaluation.ScoreBreakdown.TryGetValue(key, out var value)
            ? Math.Clamp(value, 0f, 1f)
            : 0f;

    private static IReadOnlyList<BasicsExecutionBootstrapCandidateTrace> BuildBootstrapCandidateTraces(
        IReadOnlyList<PopulationMember> population,
        int? currentGeneration)
    {
        if (!currentGeneration.HasValue || currentGeneration.Value <= 0)
        {
            return Array.Empty<BasicsExecutionBootstrapCandidateTrace>();
        }

        return population
            .Where(member =>
                member.BootstrapOrigin is not null
                && member.LastEvaluation is not null
                && member.EvaluationGeneration == currentGeneration.Value)
            .Select(member => new BasicsExecutionBootstrapCandidateTrace(
                member.BootstrapOrigin!,
                member.Definition.ToSha256Hex(),
                NormalizeSpeciesId(member.SpeciesId),
                member.LastEvaluation!.Accuracy,
                member.LastEvaluation.Fitness,
                new Dictionary<string, float>(member.LastEvaluation.ScoreBreakdown, StringComparer.Ordinal),
                member.LastEvaluation.Diagnostics.ToArray(),
                currentGeneration.Value))
            .OrderBy(trace => trace.Origin.IsUploadedSeed ? 0 : 1)
            .ThenBy(trace => trace.Origin.IsExactCopy ? 0 : 1)
            .ThenBy(trace => trace.Origin.BootstrapTemplateIndex)
            .ThenBy(trace => trace.Origin.ExactCopyOrdinal ?? int.MaxValue)
            .ThenByDescending(trace => trace.ScoreBreakdown.TryGetValue("balanced_tolerance_accuracy", out var balanced) ? Math.Clamp(balanced, 0f, 1f) : 0f)
            .ThenByDescending(trace => trace.Accuracy)
            .ToArray();
    }

    private static IReadOnlyList<BasicsExecutionBootstrapCandidateTrace> CloneBootstrapCandidateTraces(
        IReadOnlyList<BasicsExecutionBootstrapCandidateTrace> traces)
        => traces
            .Select(trace => new BasicsExecutionBootstrapCandidateTrace(
                trace.Origin,
                trace.ArtifactSha256,
                trace.SpeciesId,
                trace.Accuracy,
                trace.Fitness,
                new Dictionary<string, float>(trace.ScoreBreakdown, StringComparer.Ordinal),
                trace.Diagnostics.ToArray(),
                trace.Generation))
            .ToArray();

    private static BasicsExecutionBestCandidateSummary BuildBestCandidateSummary(
        CandidateSelection candidate,
        bool includeWinnerRuntimeState)
        => new(
            candidate.Member.Definition.Clone(),
            includeWinnerRuntimeState ? candidate.Member.SnapshotArtifact?.Clone() : null,
            includeWinnerRuntimeState && candidate.Member.ActiveBrainId != Guid.Empty ? candidate.Member.ActiveBrainId : null,
            NormalizeSpeciesId(candidate.Member.SpeciesId),
            candidate.Evaluation.Accuracy,
            candidate.Evaluation.Fitness,
            candidate.Member.Complexity,
            new Dictionary<string, float>(candidate.Evaluation.ScoreBreakdown, StringComparer.Ordinal),
            candidate.Evaluation.Diagnostics.ToArray(),
            candidate.Member.EvaluationGeneration ?? 0,
            candidate.Evaluation.AverageReadyTickCount,
            candidate.Evaluation.MinReadyTickCount,
            candidate.Evaluation.MedianReadyTickCount,
            candidate.Evaluation.MaxReadyTickCount,
            candidate.Evaluation.ReadyTickStdDev)
        {
            BootstrapOrigin = candidate.Member.BootstrapOrigin
        };

    private CandidateSelection SelectWinningCandidate(
        IReadOnlyList<CandidateSelection> candidates,
        BasicsExecutionStopCriteria? stopCriteria)
    {
        var stopTargetMatches = stopCriteria is null
            ? Array.Empty<CandidateSelection>()
            : candidates
                .Where(candidate => stopCriteria.IsSatisfied(candidate.Evaluation.Accuracy, candidate.Evaluation.Fitness))
                .ToArray();
        if (stopTargetMatches.Length > 0)
        {
            return stopTargetMatches
                .OrderBy(candidate => candidate.Member.Complexity?.InternalNeuronCount ?? int.MaxValue)
                .ThenBy(candidate => candidate.Member.Complexity?.AxonCount ?? int.MaxValue)
                .ThenBy(candidate => candidate.Member.Complexity?.ActiveInternalRegionCount ?? int.MaxValue)
                .ThenBy(candidate => candidate.TieBreakRank)
                .First();
        }

        return candidates
            .OrderByDescending(candidate => candidate.Evaluation.Fitness)
            .ThenByDescending(candidate => ResolveCandidateSelectionAccuracy(candidate.Evaluation))
            .ThenByDescending(candidate => candidate.Evaluation.Accuracy)
            .ThenBy(candidate => candidate.Member.Complexity?.InternalNeuronCount ?? int.MaxValue)
            .ThenBy(candidate => candidate.Member.Complexity?.AxonCount ?? int.MaxValue)
            .ThenBy(candidate => candidate.Member.Complexity?.ActiveInternalRegionCount ?? int.MaxValue)
            .ThenBy(candidate => candidate.TieBreakRank)
            .First();
    }

    private static CandidateSelection SelectTrackedBestCandidate(IReadOnlyList<CandidateSelection> candidates)
        => candidates
            .OrderByDescending(candidate => candidate.Evaluation.Diagnostics.Count == 0)
            .ThenByDescending(candidate => ResolveCandidateSelectionAccuracy(candidate.Evaluation))
            .ThenByDescending(candidate => candidate.Evaluation.Accuracy)
            .ThenByDescending(candidate => candidate.Evaluation.Fitness)
            .ThenBy(candidate => candidate.Member.Complexity?.InternalNeuronCount ?? int.MaxValue)
            .ThenBy(candidate => candidate.Member.Complexity?.AxonCount ?? int.MaxValue)
            .ThenBy(candidate => candidate.Member.Complexity?.ActiveInternalRegionCount ?? int.MaxValue)
            .ThenBy(candidate => candidate.TieBreakRank)
            .First();

    private static int ComputeTieBreakRank(string artifactSha)
    {
        unchecked
        {
            uint hash = 2166136261u ^ 1701u;
            foreach (var ch in artifactSha)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private static float ResolveCandidateSelectionAccuracy(BasicsTaskEvaluationResult evaluation)
        => ResolveCandidateSelectionAccuracy(evaluation.Accuracy, evaluation.ScoreBreakdown);

    private static float ResolveCandidateSelectionAccuracy(BasicsExecutionBestCandidateSummary candidate)
        => ResolveCandidateSelectionAccuracy(candidate.Accuracy, candidate.ScoreBreakdown);

    private static float ResolveCandidateSelectionAccuracy(
        float fallbackAccuracy,
        IReadOnlyDictionary<string, float> scoreBreakdown)
        => scoreBreakdown.TryGetValue("balanced_tolerance_accuracy", out var balancedAccuracy)
            ? Math.Clamp(balancedAccuracy, 0f, 1f)
            : Math.Clamp(fallbackAccuracy, 0f, 1f);

    private static string BuildGenerationDetail(
        int generation,
        GenerationMetrics metrics,
        BasicsExecutionGenerationTimingSummary? timing)
    {
        var summary = $"Generation {generation}: accuracy={metrics.BestAccuracy:0.###}, best_fitness={metrics.BestFitness:0.###}, offspring_accuracy={metrics.OffspringBestAccuracy:0.###}, offspring_fitness={metrics.OffspringBestFitness:0.###}, offspring_evaluated={metrics.OffspringEvaluatedCount}, retained={metrics.RetainedEvaluationCount}, mean_fitness={metrics.MeanFitness:0.###}, species={metrics.SpeciesCount}.";
        if (metrics.BestBalancedAccuracy > 0f
            || metrics.OffspringBestBalancedAccuracy > 0f
            || metrics.OffspringBestEdgeAccuracy > 0f
            || metrics.OffspringBestInteriorAccuracy > 0f)
        {
            summary += $" Balanced accuracy={metrics.BestBalancedAccuracy:0.###}, offspring balanced={metrics.OffspringBestBalancedAccuracy:0.###}, offspring edge={metrics.OffspringBestEdgeAccuracy:0.###}, offspring interior={metrics.OffspringBestInteriorAccuracy:0.###}.";
        }

        var bootstrapTraceDetail = BuildBootstrapTraceDetail(metrics.BootstrapCandidateTraces);
        if (!string.IsNullOrWhiteSpace(bootstrapTraceDetail))
        {
            summary += $" {bootstrapTraceDetail}";
        }

        if (metrics.TrackedBestCandidate?.MinReadyTickCount is float minReadyTick
            && metrics.TrackedBestCandidate.MedianReadyTickCount is float medianReadyTick
            && metrics.TrackedBestCandidate.MaxReadyTickCount is float maxReadyTick)
        {
            summary += $" Ready ticks (tracked best)={minReadyTick:0.##}/{medianReadyTick:0.##}/{maxReadyTick:0.##}.";
        }

        if (metrics.EvaluationFailureCount > 0 && !string.IsNullOrWhiteSpace(metrics.EvaluationFailureSummary))
        {
            summary += $" Evaluation failures: {metrics.EvaluationFailureSummary}.";
        }

        if (timing is not null)
        {
            summary += $" Timing: total={FormatSeconds(timing.TotalDurationSeconds)}, avg_batch={FormatSeconds(timing.AverageBatchDurationSeconds)}, queue={FormatSeconds(timing.AverageQueueWaitSeconds)}/brain, spawn={FormatSeconds(timing.AverageSpawnRequestSeconds)}/brain, placement={FormatSeconds(timing.AveragePlacementWaitSeconds)}/brain, setup={FormatSeconds(timing.AverageSetupSeconds)}/brain, observe={FormatSeconds(timing.AverageObservationSeconds)}/brain.";
        }

        return summary;
    }

    private static string BuildBootstrapTraceDetail(IReadOnlyList<BasicsExecutionBootstrapCandidateTrace> traces)
    {
        if (traces.Count == 0)
        {
            return string.Empty;
        }

        var uploadedExact = traces
            .Where(trace => trace.Origin.Kind == BasicsBootstrapOriginKind.UploadedExactCopy)
            .Select(trace =>
            {
                var balanced = trace.ScoreBreakdown.TryGetValue("balanced_tolerance_accuracy", out var value)
                    ? Math.Clamp(value, 0f, 1f)
                    : trace.Accuracy;
                var hash = string.IsNullOrWhiteSpace(trace.Origin.SourceContentHash)
                    ? "unknown"
                    : trace.Origin.SourceContentHash[..Math.Min(12, trace.Origin.SourceContentHash.Length)];
                var copySuffix = trace.Origin.ExactCopyOrdinal is int ordinal ? $" #{ordinal}" : string.Empty;
                return $"{trace.Origin.SourceDisplayName}{copySuffix}={balanced:0.###} ({hash})";
            })
            .Take(4)
            .ToArray();
        if (uploadedExact.Length > 0)
        {
            return $"Uploaded exact seeds: {string.Join(", ", uploadedExact)}.";
        }

        var bootstrapSummary = traces
            .Select(trace => $"{trace.Origin.SourceDisplayName}={trace.Accuracy:0.###}")
            .Take(4)
            .ToArray();
        return bootstrapSummary.Length == 0
            ? string.Empty
            : $"Bootstrap traces: {string.Join(", ", bootstrapSummary)}.";
    }

    private static string BuildStopTargetDetail(
        int generation,
        string taskDisplayName,
        BasicsExecutionStopCriteria stopCriteria,
        BasicsExecutionBestCandidateSummary? winningCandidate)
    {
        var summary = stopCriteria.RequireBothTargets
            ? $"Generation {generation} met the {taskDisplayName} stop target (accuracy >= {stopCriteria.TargetAccuracy:0.###} and fitness >= {stopCriteria.TargetFitness:0.###})."
            : $"Generation {generation} met the {taskDisplayName} stop target (accuracy >= {stopCriteria.TargetAccuracy:0.###} or fitness >= {stopCriteria.TargetFitness:0.###}).";
        if (winningCandidate?.Complexity is not null)
        {
            summary += $" Retained winner simplicity: internal_neurons={winningCandidate.Complexity.InternalNeuronCount}, axons={winningCandidate.Complexity.AxonCount}, internal_regions={winningCandidate.Complexity.ActiveInternalRegionCount}.";
        }

        if (winningCandidate?.HasSnapshotArtifact == true)
        {
            summary += " Snapshot export is available.";
        }
        else if (winningCandidate?.HasRetainedBrain == true)
        {
            summary += " Definition export is available.";
        }

        return summary;
    }

    private static string BuildGenerationLimitDetail(
        int generation,
        BasicsExecutionStopCriteria stopCriteria,
        BasicsExecutionBestCandidateSummary? bestCandidate)
    {
        var summary = stopCriteria.MaximumGenerations.HasValue
            ? $"Generation {generation} reached the configured maximum of {stopCriteria.MaximumGenerations.Value} generation(s)."
            : $"Generation {generation} reached the configured generation limit.";
        if (bestCandidate is not null)
        {
            summary += $" Best-so-far accuracy={bestCandidate.Accuracy:0.###}, fitness={bestCandidate.Fitness:0.###}.";
            if (bestCandidate.Complexity is not null)
            {
                summary += $" Simplicity snapshot: internal_neurons={bestCandidate.Complexity.InternalNeuronCount}, axons={bestCandidate.Complexity.AxonCount}, internal_regions={bestCandidate.Complexity.ActiveInternalRegionCount}.";
            }

            if (bestCandidate.HasSnapshotArtifact)
            {
                summary += " Snapshot export is available.";
            }
            else if (bestCandidate.HasRetainedBrain)
            {
                summary += " Definition export is available.";
            }
        }

        return summary;
    }

    private static string NormalizeSpeciesId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "species.default" : value.Trim();

    private readonly record struct RankedParent(PopulationMember Member, BasicsParentSelectionScore Score);

    private readonly record struct QueuedBreedingPair(
        PopulationMember ParentA,
        PopulationMember ParentB,
        float NormalizedFitnessHint,
        float NormalizedNoveltyHint,
        string PairingReason);

    private readonly record struct FrontierChildCandidate(
        PopulationMember Member,
        float NormalizedFitnessHint,
        float NormalizedNoveltyHint);

    private readonly record struct CandidateSelection(
        PopulationMember Member,
        BasicsTaskEvaluationResult Evaluation,
        int TieBreakRank);

    private readonly record struct GenerationMetrics(
        float OffspringBestAccuracy,
        float BestAccuracy,
        float OffspringBestBalancedAccuracy,
        float BestBalancedAccuracy,
        float OffspringBestEdgeAccuracy,
        float OffspringBestInteriorAccuracy,
        float OffspringBestFitness,
        float BestFitness,
        int OffspringEvaluatedCount,
        int RetainedEvaluationCount,
        double MeanFitness,
        int SpeciesCount,
        float CapacityUtilization,
        BasicsExecutionBestCandidateSummary? BestCandidate,
        BasicsExecutionBestCandidateSummary? TrackedBestCandidate,
        int EvaluationFailureCount,
        string EvaluationFailureSummary,
        IReadOnlyList<BasicsExecutionBootstrapCandidateTrace> BootstrapCandidateTraces);

    private sealed record ResolvedInitialSeedTemplate(
        ArtifactRef Definition,
        ArtifactRef? SnapshotArtifact,
        string SpeciesId,
        string SpeciesDisplayName,
        BasicsDefinitionComplexitySummary? Complexity,
        int ExactCopies)
    {
        public int BootstrapTemplateIndex { get; init; }

        public string SourceDisplayName { get; init; } = string.Empty;

        public string? SourceContentHash { get; init; }

        public BasicsBootstrapOriginKind ExactCopyOriginKind { get; init; }

        public BasicsBootstrapOriginKind VariationOriginKind { get; init; }
    }

    private readonly record struct MemberEvaluationTelemetry(
        TimeSpan QueueWait,
        TimeSpan SpawnRequest,
        TimeSpan PlacementWait,
        TimeSpan Setup,
        TimeSpan Observation,
        TimeSpan Total,
        string FailureCategory);

    private readonly record struct MemberEvaluationResult(
        PopulationMember Member,
        MemberEvaluationTelemetry Telemetry);

    private enum InFlightBrainPhase
    {
        WaitingForSpawnSlot = 0,
        RequestingSpawn = 1,
        WaitingForPlacement = 2,
        WaitingForSetupSlot = 3,
        ConfiguringRuntime = 4,
        EvaluatingSamples = 5
    }

    private readonly record struct InFlightBrainProgress(
        int BatchBrainOrdinal,
        int BatchBrainCount,
        int AttemptNumber,
        int MaxAttempts,
        string DefinitionShaShort,
        InFlightBrainPhase Phase);

    private sealed record PopulationEvaluationResult(
        IReadOnlyList<PopulationMember> Population,
        BasicsExecutionBatchTimingSummary? LatestBatchTiming,
        BasicsExecutionGenerationTimingSummary? GenerationTiming);

    private sealed record PopulationMember(
        ArtifactRef Definition,
        string SpeciesId,
        string SpeciesDisplayName,
        BasicsDefinitionComplexitySummary? Complexity,
        BasicsTaskEvaluationResult? LastEvaluation,
        int? EvaluationGeneration = null,
        bool CountsTowardOffspringMetrics = false,
        Guid ActiveBrainId = default,
        ArtifactRef? SnapshotArtifact = null)
    {
        public BasicsBootstrapOrigin? BootstrapOrigin { get; init; }
    }
}
