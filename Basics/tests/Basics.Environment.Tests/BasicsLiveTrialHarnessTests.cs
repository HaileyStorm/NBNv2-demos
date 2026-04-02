using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;
using Nbn.Proto;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Proto.Speciation;
using Nbn.Shared;
using Repro = Nbn.Proto.Repro;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsLiveTrialHarnessTests
{
    [Fact]
    public async Task LiveTrialHarness_StopsAfterRequiredSuccessfulTrials()
    {
        var runtimeClients = new List<FakeHarnessRuntimeClient>();
        var runners = new Queue<ScriptedExecutionRunner>(new[]
        {
            new ScriptedExecutionRunner(CreateSuccessfulSnapshots(generation: 3)),
            new ScriptedExecutionRunner(CreateSuccessfulSnapshots(generation: 4)),
            new ScriptedExecutionRunner(CreateSuccessfulSnapshots(generation: 5))
        });

        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) =>
            {
                var client = new FakeHarnessRuntimeClient();
                runtimeClients.Add(client);
                return Task.FromResult<IBasicsRuntimeClient>(client);
            },
            executionRunnerFactory: (_, _) => runners.Dequeue());

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 5, requiredSuccessfulTrials: 2),
            new AndTaskPlugin());

        Assert.True(report.StabilityTargetMet);
        Assert.Equal(2, report.ExecutedTrialCount);
        Assert.All(report.Trials, static trial => Assert.Equal(BasicsLiveTrialOutcome.Succeeded, trial.Outcome));
        Assert.Equal(2, runtimeClients.Count);
        Assert.Equal("nbn.basics.harness.trial01", runtimeClients[0].ConnectedClientName);
        Assert.Equal("nbn.basics.harness.trial02", runtimeClients[1].ConnectedClientName);
    }

    [Fact]
    public async Task LiveTrialHarness_AutoTunesAfterFailures()
    {
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient()),
            executionRunnerFactory: (_, _) => new ScriptedExecutionRunner(new[]
            {
                CreateSnapshot(
                    BasicsExecutionState.Running,
                    statusText: "Evaluating generation 1...",
                    detailText: "Generation 1 is running.",
                    generation: 1,
                    speciesCount: 1,
                    evaluationFailureCount: 4,
                    evaluationFailureSummary: "output_timeout_or_width_mismatch x4",
                    bestAccuracy: 0f,
                    bestFitness: 0f),
                CreateSnapshot(
                    BasicsExecutionState.Failed,
                    statusText: "Execution failed.",
                    detailText: "Generation 1 timed out.",
                    generation: 1,
                    speciesCount: 1,
                    evaluationFailureCount: 4,
                    evaluationFailureSummary: "output_timeout_or_width_mismatch x4",
                    bestAccuracy: 0f,
                    bestFitness: 0f)
            }));

        var options = CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1) with
        {
            Environment = CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1).Environment with
            {
                OutputObservationMode = BasicsOutputObservationMode.EventedOutput,
                SizingOverrides = new BasicsSizingOverrides
                {
                    InitialPopulationCount = 256,
                    MaxConcurrentBrains = 128,
                    ReproductionRunCount = 8
                }
            }
        };

        var report = await harness.RunAsync(options, new AndTaskPlugin());

        Assert.False(report.StabilityTargetMet);
        Assert.Single(report.Trials);
        var decision = Assert.Single(report.Trials).TuningDecision;
        Assert.NotNull(decision);
        Assert.True(decision!.Applied);
        Assert.Contains("output_mode=continuous_potential", decision.Changes);
        Assert.Contains("initial_population=192", decision.Changes);
        Assert.Contains("max_concurrent=96", decision.Changes);
        Assert.Equal(BasicsOutputObservationMode.VectorPotential, report.FinalConfiguration.OutputObservationMode);
        Assert.Equal(192, report.FinalConfiguration.Sizing.InitialPopulationCount);
        Assert.Equal(96, report.FinalConfiguration.Sizing.MaxConcurrentBrains);
    }

    [Fact]
    public async Task LiveTrialHarness_TreatsCanceledStoppedRunAsTimeout_AndReducesSizing()
    {
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient()),
            executionRunnerFactory: (_, _) => new CancelingStoppedExecutionRunner(
                CreateSnapshot(
                    BasicsExecutionState.Stopped,
                    statusText: "Execution stopped.",
                    detailText: "The run was canceled by the operator.",
                    generation: 1,
                    speciesCount: 1,
                    bestAccuracy: 0.75f,
                    bestFitness: 0.764f)));

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1) with
            {
                TrialTimeout = TimeSpan.FromMilliseconds(200)
            },
            new AndTaskPlugin());

        var trial = Assert.Single(report.Trials);
        Assert.Equal(BasicsLiveTrialOutcome.TimedOut, trial.Outcome);
        Assert.Equal("trial_timeout", trial.OutcomeDetail);
        Assert.NotNull(trial.TuningDecision);
        Assert.True(trial.TuningDecision!.Applied);
        Assert.Contains("initial_population=192", trial.TuningDecision.Changes);
        Assert.Contains("max_concurrent=96", trial.TuningDecision.Changes);
    }

    [Fact]
    public async Task LiveTrialHarness_ExpandsVariationBand_WhenSpeciesCollapsePersists()
    {
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient()),
            executionRunnerFactory: (_, _) => new ScriptedExecutionRunner(new[]
            {
                CreateSnapshot(
                    BasicsExecutionState.Failed,
                    statusText: "Execution failed.",
                    detailText: "Species collapsed.",
                    generation: 1,
                    speciesCount: 1,
                    bestAccuracy: 0.75f,
                    bestFitness: 0.80f)
            }));

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1),
            new AndTaskPlugin());

        var decision = Assert.Single(report.Trials).TuningDecision;
        Assert.NotNull(decision);
        Assert.True(decision!.Applied);
        Assert.Contains("diversity_weight=0.4", decision.Changes);
        Assert.Contains("exploration_fraction=0.3", decision.Changes);
        Assert.Contains("diversity_boost=0.4", decision.Changes);
        Assert.Contains("max_internal_neuron_delta=3", decision.Changes);
        Assert.Contains("max_axon_delta=10", decision.Changes);
        Assert.Contains("max_strength_delta=6", decision.Changes);
        Assert.Contains("max_parameter_delta=6", decision.Changes);
        Assert.Contains("allow_function_mutation=true", decision.Changes);
    }

    [Fact]
    public async Task LiveTrialHarness_ReturnsConnectFailed_WhenConnectRetryWindowExpires()
    {
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient
            {
                ConnectAckToReturn = null
            }),
            executionRunnerFactory: (_, _) => throw new InvalidOperationException("Execution runner should not be created when connect never succeeds."));

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1) with
            {
                TrialTimeout = TimeSpan.FromSeconds(2)
            },
            new AndTaskPlugin());

        var trial = Assert.Single(report.Trials);
        Assert.Equal(BasicsLiveTrialOutcome.ConnectFailed, trial.Outcome);
        Assert.Equal("connect_failed", trial.OutcomeDetail);
    }

    [Fact]
    public async Task LiveTrialHarness_ReturnsConnectFailed_WhenConnectWindowCancellationThrows()
    {
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient
            {
                ConnectBehavior = async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new ConnectAck { ServerName = "nbn.io", ServerTimeMs = 1 };
                }
            }),
            executionRunnerFactory: (_, _) => throw new InvalidOperationException("Execution runner should not be created when connect never succeeds."));

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1) with
            {
                TrialTimeout = TimeSpan.FromSeconds(20)
            },
            new AndTaskPlugin());

        var trial = Assert.Single(report.Trials);
        Assert.Equal(BasicsLiveTrialOutcome.ConnectFailed, trial.Outcome);
        Assert.Equal("connect_failed", trial.OutcomeDetail);
    }

    [Fact]
    public async Task LiveTrialHarness_TreatsTrialTimeoutDuringConnectAsTimedOut()
    {
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient
            {
                ConnectBehavior = async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new ConnectAck { ServerName = "nbn.io", ServerTimeMs = 1 };
                }
            }),
            executionRunnerFactory: (_, _) => throw new InvalidOperationException("Execution runner should not be created when connect never succeeds."));

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1) with
            {
                TrialTimeout = TimeSpan.FromMilliseconds(200)
            },
            new AndTaskPlugin());

        var trial = Assert.Single(report.Trials);
        Assert.Equal(BasicsLiveTrialOutcome.TimedOut, trial.Outcome);
        Assert.Equal("trial_timeout", trial.OutcomeDetail);
    }

    [Fact]
    public async Task LiveTrialHarness_PreservesTerminalSuccess_WhenTimeoutHitsAfterTerminalSnapshot()
    {
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient()),
            executionRunnerFactory: (_, _) => new TerminalSnapshotThenTimeoutRunner(
                CreateSnapshot(
                    BasicsExecutionState.Succeeded,
                    statusText: "Execution reached a perfect candidate.",
                    detailText: "Generation 1 achieved perfect AND accuracy.",
                    generation: 1,
                    speciesCount: 1,
                    bestAccuracy: 1f,
                    bestFitness: 1f)));

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1) with
            {
                TrialTimeout = TimeSpan.FromMilliseconds(200)
            },
            new AndTaskPlugin());

        var trial = Assert.Single(report.Trials);
        Assert.Equal(BasicsLiveTrialOutcome.Succeeded, trial.Outcome);
        Assert.Equal("Generation 1 achieved perfect AND accuracy.", trial.OutcomeDetail);
        Assert.NotNull(trial.TerminalSnapshot);
        Assert.Equal(BasicsExecutionState.Succeeded, trial.TerminalSnapshot!.State);
    }

    [Fact]
    public async Task LiveTrialHarness_PreservesNonAndScoreBreakdown_InSnapshotReportPlumbing()
    {
        var plugin = new OrTaskPlugin();
        var scoreBreakdown = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["task_accuracy"] = 1f,
            ["classification_accuracy"] = 1f,
            ["truth_table_coverage"] = 1f,
            ["dataset_coverage"] = 1f,
            ["target_proximity_fitness"] = 1f
        };
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(new FakeHarnessRuntimeClient()),
            executionRunnerFactory: (_, _) => new ScriptedExecutionRunner(new[]
            {
                CreateSnapshot(
                    BasicsExecutionState.Succeeded,
                    statusText: "Execution reached a perfect candidate.",
                    detailText: "Generation 1 achieved perfect OR accuracy.",
                    generation: 1,
                    speciesCount: 1,
                    bestAccuracy: 1f,
                    bestFitness: 1f,
                    scoreBreakdown: scoreBreakdown)
            }));

        var report = await harness.RunAsync(
            CreateOptions(maxTrialCount: 1, requiredSuccessfulTrials: 1, taskPlugin: plugin),
            plugin);

        Assert.True(report.StabilityTargetMet);
        Assert.Equal("or", report.InitialConfiguration.TaskId);
        Assert.Equal("OR", report.FinalConfiguration.TaskDisplayName);

        var trial = Assert.Single(report.Trials);
        Assert.Equal("or", trial.AppliedConfiguration.TaskId);
        Assert.Equal("or", trial.PlanSummary!.TaskId);
        Assert.NotNull(trial.TerminalSnapshot);
        Assert.NotNull(trial.TerminalSnapshot!.BestCandidate);
        Assert.Equal(1f, trial.TerminalSnapshot.BestCandidate!.ScoreBreakdown["task_accuracy"]);
        Assert.Equal(1f, trial.TerminalSnapshot.BestCandidate.ScoreBreakdown["truth_table_coverage"]);
        Assert.Equal(1f, trial.Snapshots[^1].BestCandidate!.ScoreBreakdown["dataset_coverage"]);
    }

    private static BasicsLiveTrialHarnessOptions CreateOptions(
        int maxTrialCount,
        int requiredSuccessfulTrials,
        IBasicsTaskPlugin? taskPlugin = null)
    {
        taskPlugin ??= new AndTaskPlugin();

        return new()
        {
            RuntimeClient = new BasicsRuntimeClientOptions
            {
                IoAddress = "127.0.0.1:12050",
                IoGatewayName = "io-gateway"
            },
            Environment = new BasicsEnvironmentOptions
            {
                ClientName = "nbn.basics.harness",
                SelectedTask = taskPlugin.Contract,
                SeedTemplate = BasicsSeedTemplateContract.CreateDefault(),
                SizingOverrides = new BasicsSizingOverrides
                {
                    InitialPopulationCount = 256,
                    ReproductionRunCount = 8,
                    MaxConcurrentBrains = 128
                },
                OutputObservationMode = BasicsOutputObservationMode.VectorPotential,
                Reproduction = BasicsReproductionPolicy.CreateDefault(),
                Scheduling = new BasicsReproductionSchedulingPolicy
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
                }
            },
            MaxTrialCount = maxTrialCount,
            TrialTimeout = TimeSpan.FromSeconds(5),
            StabilityCriteria = new BasicsLiveTrialStabilityCriteria
            {
                TargetAccuracy = 0.99f,
                TargetFitness = 0.99f,
                RequiredSuccessfulTrials = requiredSuccessfulTrials
            }
        };
    }

    private static IReadOnlyList<BasicsExecutionSnapshot> CreateSuccessfulSnapshots(int generation)
        => new[]
        {
            CreateSnapshot(
                BasicsExecutionState.Running,
                statusText: $"Generation {generation - 1} evaluated.",
                detailText: "Harness success path.",
                generation: generation - 1,
                speciesCount: 2,
                bestAccuracy: 0.92f,
                bestFitness: 0.94f),
            CreateSnapshot(
                BasicsExecutionState.Succeeded,
                statusText: "Execution reached a perfect candidate.",
                detailText: "Harness success path.",
                generation: generation,
                speciesCount: 2,
                bestAccuracy: 1f,
                bestFitness: 1f)
        };

    private static BasicsExecutionSnapshot CreateSnapshot(
        BasicsExecutionState state,
        string statusText,
        string detailText,
        int generation,
        int speciesCount,
        int evaluationFailureCount = 0,
        string evaluationFailureSummary = "",
        float bestAccuracy = 1f,
        float bestFitness = 1f,
        IReadOnlyDictionary<string, float>? scoreBreakdown = null)
        => new(
            State: state,
            StatusText: statusText,
            DetailText: detailText,
            SpeciationEpochId: 1,
            EvaluationFailureCount: evaluationFailureCount,
            EvaluationFailureSummary: evaluationFailureSummary,
            Generation: generation,
            PopulationCount: 256,
            ActiveBrainCount: 0,
            SpeciesCount: speciesCount,
            ReproductionCalls: (ulong)Math.Max(0, generation - 1),
            ReproductionRunsObserved: (ulong)Math.Max(0, generation * 2),
            CapacityUtilization: 0.5f,
            OffspringBestAccuracy: bestAccuracy,
            BestAccuracy: bestAccuracy,
            OffspringBestFitness: bestFitness,
            BestFitness: bestFitness,
            MeanFitness: Math.Max(0f, bestFitness - 0.1f),
            EffectiveTemplateDefinition: null,
            SeedShape: new BasicsResolvedSeedShape(1, 1, 3),
            BestCandidate: new BasicsExecutionBestCandidateSummary(
                DefinitionArtifact: new string('a', 64).ToArtifactRef(256, "application/x-nbn", "http://fake-store/winner"),
                SnapshotArtifact: null,
                ActiveBrainId: null,
                SpeciesId: "species.default",
                Accuracy: bestAccuracy,
                Fitness: bestFitness,
                Complexity: new BasicsDefinitionComplexitySummary(1, 1, 3),
                ScoreBreakdown: scoreBreakdown ?? new Dictionary<string, float>(StringComparer.Ordinal)
                {
                    ["task_accuracy"] = bestAccuracy,
                    ["classification_accuracy"] = bestAccuracy
                },
                Diagnostics: Array.Empty<string>()),
            OffspringAccuracyHistory: new[] { bestAccuracy },
            AccuracyHistory: new[] { bestAccuracy },
            OffspringFitnessHistory: new[] { bestFitness },
            BestFitnessHistory: new[] { bestFitness });

    private sealed class FakeHarnessRuntimeClient : IBasicsRuntimeClient
    {
        public string? ConnectedClientName { get; private set; }
        public ConnectAck? ConnectAckToReturn { get; init; } = new ConnectAck { ServerName = "nbn.io", ServerTimeMs = 1 };
        public Func<string, CancellationToken, Task<ConnectAck?>>? ConnectBehavior { get; init; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default)
        {
            ConnectedClientName = clientName;
            if (ConnectBehavior is not null)
            {
                return await ConnectBehavior(clientName, cancellationToken).ConfigureAwait(false);
            }

            return ConnectAckToReturn;
        }

        public Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<PlacementWorkerInventoryResult?>(null);

        public Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.FromResult<BrainInfo?>(null);

        public Task<SpawnBrainViaIOAck?> SpawnBrainAsync(SpawnBrain request, CancellationToken cancellationToken = default)
            => Task.FromResult<SpawnBrainViaIOAck?>(null);

        public Task<AwaitSpawnPlacementViaIOAck?> AwaitSpawnPlacementAsync(
            Guid brainId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AwaitSpawnPlacementViaIOAck?>(null);

        public Task<BrainDefinitionReady?> ExportBrainDefinitionAsync(
            Guid brainId,
            bool rebaseOverlays,
            CancellationToken cancellationToken = default)
            => Task.FromResult<BrainDefinitionReady?>(null);

        public Task<SnapshotReady?> RequestSnapshotAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.FromResult<SnapshotReady?>(null);

        public Task SubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnsubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnsubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendInputVectorAsync(Guid brainId, IReadOnlyList<float> values, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void ResetOutputBuffer(Guid brainId)
        {
        }

        public void ResetOutputEventBuffer(Guid brainId)
        {
        }

        public Task<BasicsRuntimeOutputVector?> WaitForOutputVectorAsync(Guid brainId, ulong afterTickExclusive, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult<BasicsRuntimeOutputVector?>(null);

        public Task<BasicsRuntimeOutputEvent?> WaitForOutputEventAsync(
            Guid brainId,
            ulong afterTickExclusive,
            TimeSpan timeout,
            uint? outputIndex = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<BasicsRuntimeOutputEvent?>(null);

        public Task<IoCommandAck?> PauseBrainAsync(
            Guid brainId,
            string? reason,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "pause_brain",
                Success = true,
                Message = "queued"
            });

        public Task<IoCommandAck?> ResumeBrainAsync(
            Guid brainId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "resume_brain",
                Success = true,
                Message = "queued"
            });

        public Task<KillBrainViaIOAck?> KillBrainAsync(Guid brainId, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult<KillBrainViaIOAck?>(null);

        public Task<BrainTerminated?> WaitForBrainTerminatedAsync(
            Guid brainId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult<BrainTerminated?>(null);

        public Task<Nbn.Proto.Io.SetOutputVectorSourceAck?> SetOutputVectorSourceAsync(OutputVectorSource outputVectorSource, Guid? brainId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Nbn.Proto.Io.SetOutputVectorSourceAck?>(null);

        public Task<IoCommandAck?> SetCostEnergyEnabledAsync(
            Guid brainId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IoCommandAck?>(null);

        public Task<IoCommandAck?> SetPlasticityEnabledAsync(
            Guid brainId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IoCommandAck?>(null);

        public Task<IoCommandAck?> SetHomeostasisEnabledAsync(
            Guid brainId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IoCommandAck?>(null);

        public Task<IoCommandAck?> ResetBrainRuntimeStateAsync(
            Guid brainId,
            bool resetBuffer,
            bool resetAccumulator,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IoCommandAck?>(null);

        public Task<Repro.ReproduceResult?> ReproduceByArtifactsAsync(Repro.ReproduceByArtifactsRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<Repro.ReproduceResult?>(null);

        public Task<SpeciationAssignResponse?> AssignSpeciationAsync(SpeciationAssignRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<SpeciationAssignResponse?>(null);

        public Task<SpeciationGetConfigResponse?> GetSpeciationConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SpeciationGetConfigResponse?>(null);

        public Task<SpeciationSetConfigResponse?> SetSpeciationConfigAsync(SpeciationRuntimeConfig config, bool startNewEpoch, CancellationToken cancellationToken = default)
            => Task.FromResult<SpeciationSetConfigResponse?>(null);
    }

    private sealed class ScriptedExecutionRunner : IBasicsExecutionRunner
    {
        private readonly IReadOnlyList<BasicsExecutionSnapshot> _snapshots;

        public ScriptedExecutionRunner(IReadOnlyList<BasicsExecutionSnapshot> snapshots)
        {
            _snapshots = snapshots;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<BasicsExecutionSnapshot> RunAsync(
            BasicsEnvironmentPlan plan,
            IBasicsTaskPlugin taskPlugin,
            Action<BasicsExecutionSnapshot>? onSnapshot,
            CancellationToken cancellationToken = default)
        {
            foreach (var snapshot in _snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onSnapshot?.Invoke(snapshot);
            }

            return Task.FromResult(_snapshots[^1]);
        }
    }

    private sealed class CancelingStoppedExecutionRunner : IBasicsExecutionRunner
    {
        private readonly BasicsExecutionSnapshot _terminal;

        public CancelingStoppedExecutionRunner(BasicsExecutionSnapshot terminal)
        {
            _terminal = terminal;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<BasicsExecutionSnapshot> RunAsync(
            BasicsEnvironmentPlan plan,
            IBasicsTaskPlugin taskPlugin,
            Action<BasicsExecutionSnapshot>? onSnapshot,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                onSnapshot?.Invoke(_terminal);
                return _terminal;
            }

            return _terminal;
        }
    }

    private sealed class TerminalSnapshotThenTimeoutRunner : IBasicsExecutionRunner
    {
        private readonly BasicsExecutionSnapshot _terminal;

        public TerminalSnapshotThenTimeoutRunner(BasicsExecutionSnapshot terminal)
        {
            _terminal = terminal;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<BasicsExecutionSnapshot> RunAsync(
            BasicsEnvironmentPlan plan,
            IBasicsTaskPlugin taskPlugin,
            Action<BasicsExecutionSnapshot>? onSnapshot,
            CancellationToken cancellationToken = default)
        {
            onSnapshot?.Invoke(_terminal);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return _terminal;
        }
    }
}
