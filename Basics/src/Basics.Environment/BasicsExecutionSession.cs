using Nbn.Proto;
using Nbn.Proto.Control;
using Nbn.Runtime.Artifacts;
using Nbn.Shared;
using Repro = Nbn.Proto.Repro;
using ProtoSpec = Nbn.Proto.Speciation;

namespace Nbn.Demos.Basics.Environment;

public sealed class BasicsExecutionSession : IAsyncDisposable
{
    private static readonly TimeSpan EvaluationOutputTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EvaluationRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly IReadOnlyList<float> PrimeInputVector = new[] { 0f, 0f };
    private const int MaxObservationAttempts = 3;

    private readonly IBasicsRuntimeClient _runtimeClient;
    private readonly BasicsTemplatePublishingOptions _publishingOptions;
    private readonly ReachableArtifactStorePublisher _artifactPublisher = new();
    private readonly Random _random = new(1701);

    public BasicsExecutionSession(
        IBasicsRuntimeClient runtimeClient,
        BasicsTemplatePublishingOptions publishingOptions)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        _publishingOptions = publishingOptions ?? throw new ArgumentNullException(nameof(publishingOptions));
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
        var accuracyHistory = new List<float>();
        var fitnessHistory = new List<float>();
        ulong reproductionCalls = 0;
        ulong reproductionRunsObserved = 0;

        PublishSnapshot(
            onSnapshot,
            BasicsExecutionState.Starting,
            statusText: "Starting Basics session...",
            detailText: $"Preparing template-seeded {plan.SelectedTask.DisplayName} population.",
            speciationEpochId: speciationEpochId,
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
            fitnessHistory);

        try
        {
            var template = await ResolveTemplateDefinitionAsync(plan.SeedTemplate, cancellationToken).ConfigureAwait(false);
            effectiveTemplateDefinition = template.TemplateDefinition;
            seedShape = template.SeedShape;

            PublishSnapshot(
                onSnapshot,
                BasicsExecutionState.Starting,
                statusText: "Preparing speciation epoch...",
                detailText: "Starting a fresh speciation epoch for this Basics run.",
                speciationEpochId: speciationEpochId,
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
                fitnessHistory);
            speciationEpochId = await EnsureFreshSpeciationEpochAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureOutputObservationModeAsync(plan.OutputObservationMode, cancellationToken).ConfigureAwait(false);

            var targetPopulation = Math.Max(1, plan.Capacity.RecommendedInitialPopulationCount);
            var population = await SeedInitialPopulationAsync(
                    plan,
                    template.TemplateDefinition,
                    targetPopulation,
                    cancellationToken)
                .ConfigureAwait(false);

            if (population.Count == 0)
            {
                return CreateFinalSnapshot(
                    onSnapshot,
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
                    fitnessHistory);
            }

            var generation = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                generation++;
                population = await EvaluatePopulationAsync(
                        plan,
                        taskPlugin,
                        generation,
                        population,
                        plan.OutputObservationMode,
                        speciationEpochId,
                        effectiveTemplateDefinition,
                        seedShape,
                        reproductionCalls,
                        reproductionRunsObserved,
                        accuracyHistory,
                        fitnessHistory,
                        onSnapshot,
                        cancellationToken)
                    .ConfigureAwait(false);

                var generationMetrics = BuildGenerationMetrics(population);
                accuracyHistory.Add(generationMetrics.BestAccuracy);
                fitnessHistory.Add(generationMetrics.BestFitness);

                var generationSummary = CreateSnapshot(
                    BasicsExecutionState.Running,
                    $"Generation {generation} evaluated.",
                    BuildGenerationDetail(generation, generationMetrics),
                    speciationEpochId,
                    generation,
                    population,
                    activeBrainCount: 0,
                    reproductionCalls,
                    reproductionRunsObserved,
                    effectiveTemplateDefinition,
                    seedShape,
                    accuracyHistory,
                    fitnessHistory);
                onSnapshot?.Invoke(generationSummary);

                if (generationMetrics.BestAccuracy >= 1f && generationMetrics.BestFitness >= 0.999f)
                {
                    return CreateFinalSnapshot(
                        onSnapshot,
                        BasicsExecutionState.Succeeded,
                        "Execution reached a perfect candidate.",
                        $"Generation {generation} achieved perfect {taskPlugin.Contract.DisplayName} accuracy.",
                        speciationEpochId,
                        generation,
                        population,
                        0,
                        reproductionCalls,
                        reproductionRunsObserved,
                        effectiveTemplateDefinition,
                        seedShape,
                        accuracyHistory,
                        fitnessHistory);
                }

                var nextGeneration = await BreedNextGenerationAsync(
                        plan,
                        population,
                        targetPopulation,
                        cancellationToken,
                        onBreedProgress: (detail, activePopulationCount) =>
                        {
                            onSnapshot?.Invoke(CreateSnapshot(
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
                                fitnessHistory));
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
                        onSnapshot,
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
                        fitnessHistory);
                }

                population = nextGeneration;
            }

            return CreateFinalSnapshot(
                onSnapshot,
                BasicsExecutionState.Stopped,
                "Execution stopped.",
                "The run was canceled by the operator.",
                speciationEpochId,
                accuracyHistory.Count,
                population,
                0,
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CreateFinalSnapshot(
                onSnapshot,
                BasicsExecutionState.Stopped,
                "Execution stopped.",
                "The run was canceled by the operator.",
                speciationEpochId,
                accuracyHistory.Count,
                Array.Empty<PopulationMember>(),
                0,
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory);
        }
        catch (Exception ex)
        {
            return CreateFinalSnapshot(
                onSnapshot,
                BasicsExecutionState.Failed,
                "Execution failed.",
                ex.GetBaseException().Message,
                speciationEpochId,
                accuracyHistory.Count,
                Array.Empty<PopulationMember>(),
                0,
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory);
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

    private async Task ConfigureOutputObservationModeAsync(
        BasicsOutputObservationMode mode,
        CancellationToken cancellationToken)
    {
        if (!mode.UsesVectorSubscription())
        {
            return;
        }

        var ack = await _runtimeClient.SetOutputVectorSourceAsync(mode.ResolveVectorSource(), cancellationToken).ConfigureAwait(false);
        if (ack is null)
        {
            throw new InvalidOperationException("Output vector source update returned no response.");
        }

        if (!ack.Success)
        {
            throw new InvalidOperationException(
                $"Output vector source update failed: {ack.FailureReasonCode} {ack.FailureMessage}".Trim());
        }
    }

    private async Task<(ArtifactRef TemplateDefinition, BasicsResolvedSeedShape? SeedShape)> ResolveTemplateDefinitionAsync(
        BasicsSeedTemplateContract template,
        CancellationToken cancellationToken)
    {
        if (template.TemplateDefinition is not null)
        {
            return (template.TemplateDefinition.Clone(), null);
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
        return (publication.ArtifactRef.Clone(), build.Shape);
    }

    private async Task<List<PopulationMember>> SeedInitialPopulationAsync(
        BasicsEnvironmentPlan plan,
        ArtifactRef templateDefinition,
        int targetPopulation,
        CancellationToken cancellationToken)
    {
        var bootstrapSpeciesId = BuildBootstrapSpeciesId(plan.SeedTemplate.TemplateId);
        var bootstrapSpeciesDisplayName = $"{plan.SeedTemplate.TemplateId} bootstrap";

        var templateMember = new PopulationMember(
            templateDefinition.Clone(),
            bootstrapSpeciesId,
            bootstrapSpeciesDisplayName,
            LastEvaluation: null);
        await CommitArtifactMembershipAsync(
                templateMember.Definition,
                Array.Empty<ArtifactRef>(),
                explicitSpeciesId: bootstrapSpeciesId,
                explicitSpeciesDisplayName: bootstrapSpeciesDisplayName,
                decisionReason: "basics_seed_template",
                cancellationToken)
            .ConfigureAwait(false);

        var population = new List<PopulationMember> { templateMember };
        if (targetPopulation <= 1)
        {
            return population;
        }

        var seedingConfig = plan.Reproduction.Config.Clone();
        seedingConfig.SpawnChild = Repro.SpawnChildPolicy.SpawnChildNever;

        var seedResult = await _runtimeClient.ReproduceByArtifactsAsync(
                new Repro.ReproduceByArtifactsRequest
                {
                    ParentADef = templateDefinition.Clone(),
                    ParentBDef = templateDefinition.Clone(),
                    StrengthSource = plan.Reproduction.StrengthSource,
                    Config = seedingConfig,
                    Seed = NextSeed(),
                    RunCount = (uint)Math.Max(1, targetPopulation - 1)
                },
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var childDefinition in ExtractChildDefinitions(seedResult))
        {
            if (population.Count >= targetPopulation)
            {
                break;
            }

            var membership = await CommitArtifactMembershipAsync(
                    childDefinition,
                    new[] { templateDefinition, templateDefinition },
                    explicitSpeciesId: null,
                    explicitSpeciesDisplayName: null,
                    decisionReason: "basics_seed_child",
                    cancellationToken)
                .ConfigureAwait(false);
            population.Add(new PopulationMember(
                childDefinition.Clone(),
                membership.SpeciesId,
                membership.SpeciesDisplayName,
                LastEvaluation: null));
        }

        while (population.Count < targetPopulation)
        {
            population.Add(new PopulationMember(
                templateDefinition.Clone(),
                bootstrapSpeciesId,
                bootstrapSpeciesDisplayName,
                LastEvaluation: null));
        }

        return population;
    }

    private async Task<List<PopulationMember>> EvaluatePopulationAsync(
        BasicsEnvironmentPlan plan,
        IBasicsTaskPlugin taskPlugin,
        int generation,
        IReadOnlyList<PopulationMember> population,
        BasicsOutputObservationMode outputObservationMode,
        ulong? speciationEpochId,
        ArtifactRef? effectiveTemplateDefinition,
        BasicsResolvedSeedShape? seedShape,
        ulong reproductionCalls,
        ulong reproductionRunsObserved,
        IReadOnlyList<float> accuracyHistory,
        IReadOnlyList<float> fitnessHistory,
        Action<BasicsExecutionSnapshot>? onSnapshot,
        CancellationToken cancellationToken)
    {
        var evaluated = new List<PopulationMember>(population.Count);
        var maxConcurrent = Math.Max(1, plan.Capacity.RecommendedMaxConcurrentBrains);
        var chunkCount = (int)Math.Ceiling(population.Count / (double)maxConcurrent);

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = population.Skip(chunkIndex * maxConcurrent).Take(maxConcurrent).ToArray();
            onSnapshot?.Invoke(CreateSnapshot(
                BasicsExecutionState.Running,
                $"Evaluating generation {generation}...",
                $"Batch {chunkIndex + 1}/{chunkCount} with {batch.Length} active brain(s).",
                speciationEpochId,
                generation,
                evaluated.Concat(batch).ToArray(),
                activeBrainCount: batch.Length,
                reproductionCalls,
                reproductionRunsObserved,
                effectiveTemplateDefinition,
                seedShape,
                accuracyHistory,
                fitnessHistory));

            var batchEvaluations = await Task.WhenAll(
                    batch.Select(member => EvaluateMemberAsync(taskPlugin, member, outputObservationMode, cancellationToken)))
                .ConfigureAwait(false);
            evaluated.AddRange(batchEvaluations);
        }

        return evaluated;
    }

    private async Task<PopulationMember> EvaluateMemberAsync(
        IBasicsTaskPlugin taskPlugin,
        PopulationMember member,
        BasicsOutputObservationMode outputObservationMode,
        CancellationToken cancellationToken)
    {
        Guid brainId = Guid.Empty;
        try
        {
            var spawnAck = await _runtimeClient.SpawnBrainAsync(
                    new SpawnBrain
                    {
                        BrainDef = member.Definition.Clone(),
                        InputWidth = taskPlugin.Contract.InputWidth,
                        OutputWidth = taskPlugin.Contract.OutputWidth
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (spawnAck?.Ack is null || !spawnAck.Ack.BrainId.TryToGuid(out brainId) || brainId == Guid.Empty)
            {
                return member with
                {
                    LastEvaluation = CreateTransportFailure(
                        $"spawn_failed:{spawnAck?.FailureReasonCode ?? spawnAck?.Ack?.FailureReasonCode ?? "unknown"}")
                };
            }

            var geometry = BasicsIoGeometry.Validate(
                await _runtimeClient.RequestBrainInfoAsync(brainId, cancellationToken).ConfigureAwait(false));
            if (!geometry.IsValid)
            {
                return member with
                {
                    LastEvaluation = CreateTransportFailure($"geometry_invalid:{geometry.FailureReason}")
                };
            }

            if (outputObservationMode.UsesVectorSubscription())
            {
                await _runtimeClient.SubscribeOutputsVectorAsync(brainId, cancellationToken).ConfigureAwait(false);
                _runtimeClient.ResetOutputBuffer(brainId);
            }
            else
            {
                await _runtimeClient.SubscribeOutputsAsync(brainId, cancellationToken).ConfigureAwait(false);
                _runtimeClient.ResetOutputEventBuffer(brainId);
            }

            var observations = new List<BasicsTaskObservation>();
            var samples = taskPlugin.BuildDeterministicDataset();
            ulong lastTick = 0;

            if (outputObservationMode.UsesVectorSubscription())
            {
                await _runtimeClient.SendInputVectorAsync(brainId, PrimeInputVector, cancellationToken).ConfigureAwait(false);
                var baseline = await _runtimeClient.WaitForOutputVectorAsync(
                        brainId,
                        afterTickExclusive: 0,
                        timeout: EvaluationOutputTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (baseline is not null)
                {
                    lastTick = baseline.TickId;
                }
            }

            foreach (var sample in samples)
            {
                var observation = await ObserveSampleAsync(
                        brainId,
                        sample,
                        lastTick,
                        outputObservationMode,
                        taskPlugin.Contract.OutputWidth,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (observation is null)
                {
                    return member with
                    {
                        LastEvaluation = CreateTransportFailure("output_timeout_or_width_mismatch")
                    };
                }

                observations.Add(observation.Value);
                lastTick = observation.Value.TickId;
            }

            var evaluation = taskPlugin.Evaluate(
                new BasicsTaskEvaluationContext(
                    taskPlugin.Contract.InputWidth,
                    taskPlugin.Contract.OutputWidth,
                    TickAligned: taskPlugin.Contract.UsesTickAlignedEvaluation,
                    TickBase: observations.Count == 0 ? 0UL : observations[0].TickId),
                samples,
                observations);
            return member with { LastEvaluation = evaluation };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return member with
            {
                LastEvaluation = CreateTransportFailure($"evaluation_failed:{ex.GetBaseException().Message}")
            };
        }
        finally
        {
            if (brainId != Guid.Empty)
            {
                try
                {
                    if (outputObservationMode.UsesVectorSubscription())
                    {
                        await _runtimeClient.UnsubscribeOutputsVectorAsync(brainId, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _runtimeClient.UnsubscribeOutputsAsync(brainId, cancellationToken).ConfigureAwait(false);
                    }

                    await _runtimeClient.KillBrainAsync(brainId, "basics_evaluation_complete", cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort teardown only.
                }
            }
        }
    }

    private async Task<BasicsTaskObservation?> ObserveSampleAsync(
        Guid brainId,
        BasicsTaskSample sample,
        ulong lastTick,
        BasicsOutputObservationMode outputObservationMode,
        uint expectedOutputWidth,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxObservationAttempts; attempt++)
        {
            await _runtimeClient.SendInputVectorAsync(
                    brainId,
                    new[] { sample.InputA, sample.InputB },
                    cancellationToken)
                .ConfigureAwait(false);

            if (outputObservationMode.UsesVectorSubscription())
            {
                var output = await _runtimeClient.WaitForOutputVectorAsync(
                        brainId,
                        lastTick,
                        EvaluationOutputTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (output is not null && output.Values.Count >= expectedOutputWidth)
                {
                    return new BasicsTaskObservation(output.TickId, output.Values[0]);
                }
            }
            else
            {
                var output = await _runtimeClient.WaitForOutputEventAsync(
                        brainId,
                        lastTick,
                        EvaluationOutputTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (output is not null)
                {
                    return new BasicsTaskObservation(Math.Max(lastTick + 1, output.TickId), output.Value);
                }

                return new BasicsTaskObservation(lastTick + 1, 0f);
            }

            if (attempt < MaxObservationAttempts)
            {
                await Task.Delay(EvaluationRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<List<PopulationMember>> BreedNextGenerationAsync(
        BasicsEnvironmentPlan plan,
        IReadOnlyList<PopulationMember> population,
        int targetPopulation,
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

        var eliteCount = ranked.Length == 1
            ? 1
            : Math.Max(1, (int)Math.Ceiling(ranked.Length * plan.Scheduling.ParentSelection.EliteFraction));
        eliteCount = Math.Min(eliteCount, Math.Min(ranked.Length, targetPopulation));

        var nextGeneration = ranked.Take(eliteCount).Select(static member => member with { LastEvaluation = null }).ToList();
        var parentPool = BuildParentPool(plan.Scheduling.ParentSelection, population);
        if (parentPool.Count == 0)
        {
            return nextGeneration;
        }

        var bestFitness = Math.Max(0.0001f, ranked.Max(member => member.LastEvaluation?.Fitness ?? 0f));
        var maxAttempts = Math.Max(targetPopulation * 4, 8);
        var attempt = 0;
        while (nextGeneration.Count < targetPopulation && attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            var parentA = parentPool[attempt % parentPool.Count];
            var parentB = SelectPairParent(parentPool, parentA, attempt);
            var normalizedFitness = Math.Clamp(
                (((parentA.LastEvaluation?.Fitness ?? 0f) + (parentB.LastEvaluation?.Fitness ?? 0f)) / 2f) / bestFitness,
                0f,
                1f);
            var normalizedNovelty = Math.Max(
                ComputeNovelty(parentA, population),
                ComputeNovelty(parentB, population));
            var runCount = BasicsReproductionBudgetPlanner.ResolveRunCount(
                plan.Scheduling.RunAllocation,
                plan.Capacity.RecommendedReproductionRunCount,
                normalizedFitness,
                normalizedNovelty);
            onBreedProgress?.Invoke(
                $"Selected parents {parentA.SpeciesId} × {parentB.SpeciesId}; requesting {runCount} child run(s).",
                nextGeneration.Count);

            var reproduceConfig = plan.Reproduction.Config.Clone();
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

                var membership = await CommitArtifactMembershipAsync(
                        childDefinition,
                        new[] { parentA.Definition, parentB.Definition },
                        explicitSpeciesId: null,
                        explicitSpeciesDisplayName: null,
                        decisionReason: "basics_generation_child",
                        cancellationToken)
                    .ConfigureAwait(false);
                nextGeneration.Add(new PopulationMember(
                    childDefinition.Clone(),
                    membership.SpeciesId,
                    membership.SpeciesDisplayName,
                    LastEvaluation: null));
            }
        }

        return nextGeneration.Count == 0 ? ranked.Take(1).Select(static member => member with { LastEvaluation = null }).ToList() : nextGeneration;
    }

    private List<PopulationMember> BuildParentPool(
        BasicsParentSelectionPolicy policy,
        IReadOnlyList<PopulationMember> population)
    {
        if (population.Count == 0)
        {
            return new List<PopulationMember>();
        }

        var speciesCounts = population
            .GroupBy(member => NormalizeSpeciesId(member.SpeciesId))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var maxFitness = Math.Max(0.0001f, population.Max(member => member.LastEvaluation?.Fitness ?? 0f));
        var maxSpeciesCount = Math.Max(1, speciesCounts.Values.DefaultIfEmpty(1).Max());

        var ranked = population
            .Select(member =>
            {
                var speciesId = NormalizeSpeciesId(member.SpeciesId);
                var speciesCount = speciesCounts.TryGetValue(speciesId, out var count) ? count : 1;
                var novelty = 1f - (speciesCount / (float)population.Count);
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
            Math.Max(0, (int)Math.Round(population.Count * policy.ExplorationFraction, MidpointRounding.AwayFromZero)),
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

        return pool;
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
        IReadOnlyList<ArtifactRef> parents,
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
            request.Parents.Add(new ProtoSpec.SpeciationParentRef
            {
                ArtifactRef = parent.Clone()
            });
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
            speciesId = NormalizeSpeciesId(parents[0].ToSha256Hex());
        }

        var displayName = string.IsNullOrWhiteSpace(decision?.SpeciesDisplayName)
            ? (explicitSpeciesDisplayName ?? speciesId)
            : decision!.SpeciesDisplayName;
        return (speciesId, displayName);
    }

    private static IReadOnlyList<ArtifactRef> ExtractChildDefinitions(Repro.ReproduceResult? result)
    {
        var definitions = new List<ArtifactRef>();
        if (result is null)
        {
            return definitions;
        }

        if (HasArtifactRef(result.ChildDef))
        {
            definitions.Add(result.ChildDef.Clone());
        }

        foreach (var run in result.Runs)
        {
            if (HasArtifactRef(run.ChildDef))
            {
                definitions.Add(run.ChildDef.Clone());
            }
        }

        return definitions;
    }

    private static bool HasArtifactRef(ArtifactRef? artifactRef)
        => artifactRef is not null && artifactRef.TryToSha256Bytes(out _);

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
                ["classification_accuracy"] = 0f,
                ["mean_absolute_error"] = 1f,
                ["mean_squared_error"] = 1f,
                ["truth_table_coverage"] = 0f
            },
            Diagnostics: new[] { diagnostic });

    private static BasicsExecutionSnapshot CreateFinalSnapshot(
        Action<BasicsExecutionSnapshot>? onSnapshot,
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
        IReadOnlyList<float> fitnessHistory)
    {
        var snapshot = CreateSnapshot(
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
            fitnessHistory);
        onSnapshot?.Invoke(snapshot);
        return snapshot;
    }

    private static void PublishSnapshot(
        Action<BasicsExecutionSnapshot>? onSnapshot,
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
        IReadOnlyList<float> fitnessHistory)
    {
        onSnapshot?.Invoke(new BasicsExecutionSnapshot(
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
            bestAccuracy,
            bestFitness,
            meanFitness,
            effectiveTemplateDefinition,
            seedShape,
            bestCandidate,
            accuracyHistory.ToArray(),
            fitnessHistory.ToArray()));
    }

    private static BasicsExecutionSnapshot CreateSnapshot(
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
        IReadOnlyList<float> fitnessHistory)
    {
        var metrics = BuildGenerationMetrics(population);
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
            metrics.BestAccuracy,
            metrics.BestFitness,
            (float)metrics.MeanFitness,
            effectiveTemplateDefinition?.Clone(),
            seedShape,
            metrics.BestCandidate,
            accuracyHistory.ToArray(),
            fitnessHistory.ToArray());
    }

    private static GenerationMetrics BuildGenerationMetrics(IReadOnlyList<PopulationMember> population)
    {
        if (population.Count == 0)
        {
            return new GenerationMetrics(
                BestAccuracy: 0f,
                BestFitness: 0f,
                MeanFitness: 0f,
                SpeciesCount: 0,
                CapacityUtilization: 0f,
                BestCandidate: null,
                EvaluationFailureCount: 0,
                EvaluationFailureSummary: string.Empty);
        }

        var evaluated = population.Select(member => member.LastEvaluation ?? CreateTransportFailure("evaluation_missing")).ToArray();
        var bestIndex = Array.FindIndex(evaluated, evaluation =>
            evaluation.Fitness == evaluated.Max(candidate => candidate.Fitness)
            && evaluation.Accuracy == evaluated.Max(candidate => candidate.Accuracy));
        if (bestIndex < 0)
        {
            bestIndex = 0;
        }

        var bestMember = population[bestIndex];
        var bestEvaluation = evaluated[bestIndex];
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

        return new GenerationMetrics(
            BestAccuracy: evaluated.Max(candidate => candidate.Accuracy),
            BestFitness: evaluated.Max(candidate => candidate.Fitness),
            MeanFitness: evaluated.Average(candidate => candidate.Fitness),
            SpeciesCount: speciesCount,
            CapacityUtilization: 0f,
            BestCandidate: new BasicsExecutionBestCandidateSummary(
                bestMember.Definition.ToSha256Hex(),
                NormalizeSpeciesId(bestMember.SpeciesId),
                bestEvaluation.Accuracy,
                bestEvaluation.Fitness,
                new Dictionary<string, float>(bestEvaluation.ScoreBreakdown, StringComparer.Ordinal),
                bestEvaluation.Diagnostics.ToArray()),
            EvaluationFailureCount: failureCount,
            EvaluationFailureSummary: failureSummary);
    }

    private static string BuildGenerationDetail(int generation, GenerationMetrics metrics)
    {
        var summary = $"Generation {generation}: accuracy={metrics.BestAccuracy:0.###}, best_fitness={metrics.BestFitness:0.###}, mean_fitness={metrics.MeanFitness:0.###}, species={metrics.SpeciesCount}.";
        if (metrics.EvaluationFailureCount > 0 && !string.IsNullOrWhiteSpace(metrics.EvaluationFailureSummary))
        {
            summary += $" Evaluation failures: {metrics.EvaluationFailureSummary}.";
        }

        return summary;
    }

    private static string NormalizeSpeciesId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "species.default" : value.Trim();

    private readonly record struct RankedParent(PopulationMember Member, BasicsParentSelectionScore Score);

    private readonly record struct GenerationMetrics(
        float BestAccuracy,
        float BestFitness,
        double MeanFitness,
        int SpeciesCount,
        float CapacityUtilization,
        BasicsExecutionBestCandidateSummary? BestCandidate,
        int EvaluationFailureCount,
        string EvaluationFailureSummary);

    private sealed record PopulationMember(
        ArtifactRef Definition,
        string SpeciesId,
        string SpeciesDisplayName,
        BasicsTaskEvaluationResult? LastEvaluation);
}
