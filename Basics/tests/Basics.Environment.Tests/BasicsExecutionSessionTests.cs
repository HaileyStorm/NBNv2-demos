using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;
using Nbn.Proto;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Proto.Speciation;
using Nbn.Runtime.Artifacts;
using Nbn.Shared;
using Nbn.Shared.Format;
using Nbn.Shared.Validation;
using Repro = Nbn.Proto.Repro;
using System.Reflection;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsExecutionSessionTests
{
    [Fact]
    public void TemplateBuilder_CreatesValidTwoByOneArtifact_ByDefault()
    {
        var build = BasicsTemplateArtifactBuilder.Build(BasicsSeedTemplateContract.CreateDefault());

        var header = NbnBinary.ReadNbnHeader(build.Bytes);
        var sections = header.Regions
            .Select((entry, index) => (Entry: entry, RegionId: index))
            .Where(static pair => pair.Entry.NeuronSpan > 0)
            .Select(pair => NbnBinary.ReadNbnRegionSection(build.Bytes, pair.Entry.Offset))
            .ToArray();
        var validation = NbnBinaryValidator.ValidateNbn(header, sections);

        Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(static issue => issue.ToString())));
        Assert.Equal(BasicsIoGeometry.InputWidth, header.Regions[NbnConstants.InputRegionId].NeuronSpan);
        Assert.Equal(BasicsIoGeometry.OutputWidth, header.Regions[NbnConstants.OutputRegionId].NeuronSpan);
        Assert.True(build.Shape.ActiveInternalRegionCount >= 1);
        Assert.True(build.Shape.InternalNeuronCount >= 1);
        Assert.True(build.Shape.AxonCount >= 3);
    }

    [Fact]
    public void TemplateBuilder_RejectsAxonCapBelowMinimumViableTopology()
    {
        var template = BasicsSeedTemplateContract.CreateDefault() with
        {
            InitialSeedShapeConstraints = new BasicsSeedShapeConstraints
            {
                MaxAxonCount = 1
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BasicsTemplateArtifactBuilder.Build(template));
        Assert.Contains("minimum viable topology", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateBuilder_SortsAxonsPerNeuron_ByTargetRegionThenNeuron()
    {
        var build = BasicsTemplateArtifactBuilder.Build(BasicsSeedTemplateContract.CreateDefault() with
        {
            InitialSeedShapeConstraints = new BasicsSeedShapeConstraints
            {
                MinAxonCount = 5
            }
        });

        var header = NbnBinary.ReadNbnHeader(build.Bytes);
        var inputSection = NbnBinary.ReadNbnRegionSection(build.Bytes, header.Regions[NbnConstants.InputRegionId].Offset);

        var axonOffset = 0;
        foreach (var neuron in inputSection.NeuronRecords)
        {
            var outgoing = inputSection.AxonRecords.Skip((int)axonOffset).Take((int)neuron.AxonCount).ToArray();
            var sorted = outgoing
                .OrderBy(static axon => axon.TargetRegionId)
                .ThenBy(static axon => axon.TargetNeuronId)
                .ThenBy(static axon => axon.StrengthCode)
                .ToArray();
            Assert.Equal(sorted, outgoing);
            axonOffset += neuron.AxonCount;
        }
    }

    [Fact]
    public async Task ExecutionSession_PublishesTemplate_EvaluatesPopulation_AndBreedsTowardSuccess()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = new BasicsExecutionSession(
            runtimeClient,
            new BasicsTemplatePublishingOptions
            {
                BindHost = "127.0.0.1"
            });

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                new BasicsEnvironmentPlan(
                    SelectedTask: new AndTaskPlugin().Contract,
                    SeedTemplate: BasicsSeedTemplateContract.CreateDefault(),
                    Capacity: new BasicsCapacityRecommendation(
                        Source: BasicsCapacitySource.RuntimePlacementInventory,
                        EligibleWorkerCount: 1,
                        RecommendedInitialPopulationCount: 2,
                        RecommendedReproductionRunCount: 1,
                        RecommendedMaxConcurrentBrains: 1,
                        CapacityScore: 1f,
                        EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                        Summary: "test"),
                    OutputObservationMode: BasicsOutputObservationMode.VectorPotential,
                    Reproduction: BasicsReproductionPolicy.CreateDefault(),
                    Scheduling: BasicsReproductionSchedulingPolicy.Default,
                    Metrics: BasicsMetricsContract.Default,
                    StopCriteria: new BasicsExecutionStopCriteria(),
                    PlannedAtUtc: DateTimeOffset.UtcNow),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.NotNull(final.EffectiveTemplateDefinition);
            Assert.True(final.BestAccuracy >= 1f);
            Assert.True(final.BestFitness >= 1f);
            Assert.True(runtimeClient.ReproduceCallCount >= 2);
            Assert.Equal(1, runtimeClient.GetSpeciationConfigCallCount);
            Assert.Equal(1, runtimeClient.SetSpeciationConfigCallCount);
            Assert.True(runtimeClient.SpeciationEpochStartedBeforeFirstReproduce);
            Assert.Empty(runtimeClient.SetOutputVectorSourceRequests);
            Assert.True(runtimeClient.VectorSubscriptionCount > 0);
            Assert.Contains(snapshots, snapshot => snapshot.State == BasicsExecutionState.Running);
            Assert.True(final.AccuracyHistory.Count >= 2);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_SucceedsForOrTask_ThroughFakeRuntimePath()
    {
        var plugin = new OrTaskPlugin();
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = plugin.Contract.TaskId
        };
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var plan = CreatePlan(BasicsOutputObservationMode.VectorPotential, taskPlugin: plugin) with
            {
                Capacity = new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 1,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 1,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };
            var final = await session.RunAsync(
                plan,
                plugin,
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.Equal(1, final.Generation);
            Assert.True(final.BestAccuracy >= 1f);
            Assert.True(final.BestFitness >= 1f);
            Assert.NotNull(final.BestCandidate);
            Assert.Equal(1f, final.BestCandidate.ScoreBreakdown["task_accuracy"]);
            Assert.Equal(1f, final.BestCandidate.ScoreBreakdown["truth_table_coverage"]);
            Assert.Equal(0, runtimeClient.ReproduceCallCount);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_SucceedsForXorTask_WhenVectorOutputsOnlyEmitOnChange()
    {
        var plugin = new XorTaskPlugin();
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = plugin.Contract.TaskId,
            OnlyEmitOutputVectorOnChange = true
        };
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var plan = CreatePlan(BasicsOutputObservationMode.VectorPotential, taskPlugin: plugin) with
            {
                Capacity = new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 1,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 1,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };

            var final = await session.RunAsync(
                plan,
                plugin,
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.True(final.BestAccuracy > 0f);
            Assert.True(final.BestFitness > 0f);
            Assert.NotNull(final.BestCandidate);
            Assert.Equal(1f, final.BestCandidate.ScoreBreakdown["truth_table_coverage"]);
            Assert.DoesNotContain(
                final.BestCandidate.Diagnostics,
                static diagnostic => diagnostic.Contains("output_timeout_or_width_mismatch", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_UsesEventedOutputMode_WhenConfigured()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.EventedOutput),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.True(runtimeClient.SingleSubscriptionCount > 0);
            Assert.Equal(0, runtimeClient.VectorSubscriptionCount);
            Assert.Empty(runtimeClient.SetOutputVectorSourceRequests);
            Assert.NotEmpty(runtimeClient.EventWaitTimeouts);
            Assert.All(runtimeClient.EventWaitTimeouts, timeout => Assert.Equal(TimeSpan.FromSeconds(1), timeout));
            Assert.Empty(runtimeClient.VectorWaitTimeouts);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_UsesBufferVectorSource_WhenConfigured()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorBuffer),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.Contains(
                runtimeClient.SetOutputVectorSourceRequests,
                static request => request.BrainId != Guid.Empty && request.OutputVectorSource == OutputVectorSource.Buffer);
            Assert.True(runtimeClient.VectorSubscriptionCount > 0);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_StopsAtConfiguredTarget_RetainsWinner_AndPrefersSimplerStructure()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        TargetAccuracy = 0.75f,
                        TargetFitness = 0.75f
                    }),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.Equal(1, final.Generation);
            Assert.Equal(1, final.ActiveBrainCount);
            Assert.NotNull(final.EffectiveTemplateDefinition);
            Assert.NotNull(final.BestCandidate);
            Assert.True(final.BestCandidate.HasRetainedBrain);
            Assert.True(final.BestCandidate.HasSnapshotArtifact);
            Assert.Equal(final.EffectiveTemplateDefinition.ToSha256Hex(), final.BestCandidate.ArtifactSha256);
            Assert.Equal(1, runtimeClient.LiveBrainCount);
            Assert.Equal(1, runtimeClient.SnapshotRequestCount);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_PreservesLastEvaluatedMetrics_WhenFailureOccursAfterGenerationSummary()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ThrowOnReproduceCallNumber = 2
        };
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.Equal(1, final.Generation);
            Assert.True(final.BestAccuracy > 0f);
            Assert.True(final.BestFitness > 0f);
            Assert.NotEmpty(final.AccuracyHistory);
            Assert.NotEmpty(final.BestFitnessHistory);
            Assert.NotNull(final.BestCandidate);
            Assert.Contains(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Running && snapshot.StatusText.Contains("Generation 1 evaluated.", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public void TransportFailure_UsesSharedBreakdownShape_ForAllImplementedTaskFamilies()
    {
        var method = typeof(BasicsExecutionSession).GetMethod("CreateTransportFailure", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = Assert.IsType<BasicsTaskEvaluationResult>(method!.Invoke(null, new object[] { "transport_failed" }));

        Assert.Equal(0f, result.Fitness);
        Assert.Equal(0f, result.Accuracy);
        Assert.Equal(0f, result.ScoreBreakdown["task_accuracy"]);
        Assert.Equal(0f, result.ScoreBreakdown["classification_accuracy"]);
        Assert.Equal(0f, result.ScoreBreakdown["tolerance_accuracy"]);
        Assert.Equal(0f, result.ScoreBreakdown["dataset_coverage"]);
        Assert.Equal(0f, result.ScoreBreakdown["truth_table_coverage"]);
        Assert.Equal(0f, result.ScoreBreakdown["comparison_set_coverage"]);
        Assert.Equal(0f, result.ScoreBreakdown["evaluation_set_coverage"]);
        Assert.Equal(1f, result.ScoreBreakdown["negative_mean_output"]);
        Assert.Equal(1f, result.ScoreBreakdown["positive_mean_gap"]);
        Assert.Equal(1f, result.ScoreBreakdown["zero_product_mean_output"]);
        Assert.Equal(1f, result.ScoreBreakdown["unit_product_gap"]);
        Assert.Equal(1f, result.ScoreBreakdown["midrange_mean_absolute_error"]);
    }

    [Fact]
    public async Task ExecutionSession_MapsVariationBandIntoReproductionConfig()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var variation = new BasicsSeedVariationBand
            {
                MaxInternalNeuronDelta = 3,
                MaxAxonDelta = 10,
                MaxStrengthCodeDelta = 6,
                MaxParameterCodeDelta = 5,
                AllowFunctionMutation = true,
                AllowAxonReroute = false,
                AllowRegionSetChange = true
            };

            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential) with
                {
                    SeedTemplate = BasicsSeedTemplateContract.CreateDefault() with
                    {
                        InitialVariationBand = variation
                    }
                },
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.NotEmpty(runtimeClient.ReproduceRequests);
            var request = runtimeClient.ReproduceRequests[0];
            Assert.Equal((uint)3, request.Config.Limits.MaxNeuronsAddedAbs);
            Assert.Equal((uint)3, request.Config.Limits.MaxNeuronsRemovedAbs);
            Assert.Equal((uint)10, request.Config.Limits.MaxAxonsAddedAbs);
            Assert.Equal((uint)10, request.Config.Limits.MaxAxonsRemovedAbs);
            Assert.Equal((uint)1, request.Config.Limits.MaxRegionsAddedAbs);
            Assert.Equal((uint)1, request.Config.Limits.MaxRegionsRemovedAbs);
            Assert.True(request.Config.StrengthTransformEnabled);
            Assert.True(request.Config.ProbMutateFunc > 0f);
            Assert.Equal(0f, request.Config.ProbRerouteAxon);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_ThrottlesSetupConcurrency_ByEligibleWorkerCount()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            SpawnDelay = TimeSpan.FromMilliseconds(50)
        };
        var session = new BasicsExecutionSession(runtimeClient, new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" });

        try
        {
            var final = await session.RunAsync(
                new BasicsEnvironmentPlan(
                    SelectedTask: new AndTaskPlugin().Contract,
                    SeedTemplate: BasicsSeedTemplateContract.CreateDefault(),
                    Capacity: new BasicsCapacityRecommendation(
                        Source: BasicsCapacitySource.RuntimePlacementInventory,
                        EligibleWorkerCount: 1,
                        RecommendedInitialPopulationCount: 4,
                        RecommendedReproductionRunCount: 1,
                        RecommendedMaxConcurrentBrains: 4,
                        CapacityScore: 1f,
                        EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                        Summary: "test"),
                    OutputObservationMode: BasicsOutputObservationMode.VectorPotential,
                    Reproduction: BasicsReproductionPolicy.CreateDefault(),
                    Scheduling: BasicsReproductionSchedulingPolicy.Default,
                    Metrics: BasicsMetricsContract.Default,
                    StopCriteria: new BasicsExecutionStopCriteria(),
                    PlannedAtUtc: DateTimeOffset.UtcNow),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.InRange(runtimeClient.MaxObservedConcurrentSpawnRequests, 1, 2);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static BasicsEnvironmentPlan CreatePlan(
        BasicsOutputObservationMode outputObservationMode,
        BasicsExecutionStopCriteria? stopCriteria = null,
        IBasicsTaskPlugin? taskPlugin = null)
    {
        taskPlugin ??= new AndTaskPlugin();

        return new(
            SelectedTask: taskPlugin.Contract,
            SeedTemplate: BasicsSeedTemplateContract.CreateDefault(),
            Capacity: new BasicsCapacityRecommendation(
                Source: BasicsCapacitySource.RuntimePlacementInventory,
                EligibleWorkerCount: 1,
                RecommendedInitialPopulationCount: 2,
                RecommendedReproductionRunCount: 1,
                RecommendedMaxConcurrentBrains: 1,
                CapacityScore: 1f,
                EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                Summary: "test"),
            OutputObservationMode: outputObservationMode,
            Reproduction: BasicsReproductionPolicy.CreateDefault(),
            Scheduling: BasicsReproductionSchedulingPolicy.Default,
            Metrics: BasicsMetricsContract.Default,
            StopCriteria: stopCriteria ?? new BasicsExecutionStopCriteria(),
            PlannedAtUtc: DateTimeOffset.UtcNow);
    }

    private sealed class FakeBasicsRuntimeClient : IBasicsRuntimeClient
    {
        private readonly Dictionary<Guid, ArtifactRef> _brainDefinitions = new();
        private readonly Dictionary<Guid, ArtifactRef> _brainSnapshots = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputVector>> _outputs = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputEvent>> _outputEvents = new();
        private readonly Dictionary<Guid, ulong> _ticks = new();
        private readonly Dictionary<string, string> _behaviorByArtifactSha = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _artifactRoot = Path.Combine(Path.GetTempPath(), "nbn-basics-tests", Guid.NewGuid().ToString("N"));
        private readonly LocalArtifactStore _artifactStore;
        private int _childIndex;
        private bool _epochStarted;

        public FakeBasicsRuntimeClient()
        {
            Directory.CreateDirectory(_artifactRoot);
            _artifactStore = new LocalArtifactStore(new ArtifactStoreOptions(_artifactRoot));
        }

        public int ReproduceCallCount { get; private set; }
        public int GetSpeciationConfigCallCount { get; private set; }
        public int SetSpeciationConfigCallCount { get; private set; }
        public bool SpeciationEpochStartedBeforeFirstReproduce { get; private set; }
        public int VectorSubscriptionCount { get; private set; }
        public int SingleSubscriptionCount { get; private set; }
        public int SnapshotRequestCount { get; private set; }
        public int LiveBrainCount => _brainDefinitions.Count;
        public int? ThrowOnReproduceCallNumber { get; init; }
        public TimeSpan SpawnDelay { get; init; }
        public string DefaultBehavior { get; init; } = "zero";
        public bool OnlyEmitOutputVectorOnChange { get; init; }
        public int MaxObservedConcurrentSpawnRequests => _maxObservedConcurrentSpawnRequests;
        public List<(Guid BrainId, OutputVectorSource OutputVectorSource)> SetOutputVectorSourceRequests { get; } = new();
        public List<Repro.ReproduceByArtifactsRequest> ReproduceRequests { get; } = new();
        public List<TimeSpan> VectorWaitTimeouts { get; } = new();
        public List<TimeSpan> EventWaitTimeouts { get; } = new();
        private int _activeSpawnRequests;
        private int _maxObservedConcurrentSpawnRequests;
        private readonly Dictionary<Guid, float> _lastVectorOutputByBrain = new();

        public Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default)
            => Task.FromResult<ConnectAck?>(new ConnectAck { ServerName = clientName, ServerTimeMs = 1 });

        public Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<PlacementWorkerInventoryResult?>(null);

        public Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<BrainInfo?>(_brainDefinitions.ContainsKey(brainId)
                ? new BrainInfo
                {
                    BrainId = brainId.ToProtoUuid(),
                    InputWidth = BasicsIoGeometry.InputWidth,
                    OutputWidth = BasicsIoGeometry.OutputWidth
                }
                : new BrainInfo
                {
                    BrainId = brainId.ToProtoUuid(),
                    InputWidth = 0,
                    OutputWidth = 0
                });
        }

        public async Task<SpawnBrainViaIOAck?> SpawnBrainAsync(SpawnBrain request, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeSpawnRequests);
            UpdateMaxObservedConcurrentSpawnRequests(active);
            try
            {
                if (SpawnDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SpawnDelay, cancellationToken);
                }

                var brainId = Guid.NewGuid();
                _brainDefinitions[brainId] = request.BrainDef.Clone();
                _outputs[brainId] = new Queue<BasicsRuntimeOutputVector>();
                _outputEvents[brainId] = new Queue<BasicsRuntimeOutputEvent>();
                _ticks[brainId] = 0;
                _lastVectorOutputByBrain.Remove(brainId);
                return new SpawnBrainViaIOAck
                {
                    Ack = new SpawnBrainAck
                    {
                        BrainId = brainId.ToProtoUuid()
                    }
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeSpawnRequests);
            }
        }

        public Task<BrainDefinitionReady?> ExportBrainDefinitionAsync(
            Guid brainId,
            bool rebaseOverlays,
            CancellationToken cancellationToken = default)
        {
            var definition = _brainDefinitions.TryGetValue(brainId, out var artifact)
                ? artifact.Clone()
                : null;
            return Task.FromResult<BrainDefinitionReady?>(new BrainDefinitionReady
            {
                BrainId = brainId.ToProtoUuid(),
                BrainDef = definition
            });
        }

        public Task<SnapshotReady?> RequestSnapshotAsync(Guid brainId, CancellationToken cancellationToken = default)
        {
            SnapshotRequestCount++;
            if (!_brainDefinitions.ContainsKey(brainId))
            {
                return Task.FromResult<SnapshotReady?>(new SnapshotReady
                {
                    BrainId = brainId.ToProtoUuid()
                });
            }

            if (!_brainSnapshots.TryGetValue(brainId, out var snapshot))
            {
                snapshot = StoreArtifact(
                    new byte[] { 0x4E, 0x42, 0x4E, 0x53, 0x01, 0x00, 0x00, 0x00 },
                    "application/x-nbs");
                _brainSnapshots[brainId] = snapshot;
            }

            return Task.FromResult<SnapshotReady?>(new SnapshotReady
            {
                BrainId = brainId.ToProtoUuid(),
                Snapshot = snapshot.Clone()
            });
        }

        public Task SubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default)
        {
            VectorSubscriptionCount++;
            return Task.CompletedTask;
        }

        public Task SubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default)
        {
            SingleSubscriptionCount++;
            return Task.CompletedTask;
        }

        public Task UnsubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UnsubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendInputVectorAsync(Guid brainId, IReadOnlyList<float> values, CancellationToken cancellationToken = default)
        {
            var artifact = _brainDefinitions[brainId];
            var tick = ++_ticks[brainId];
            var behavior = ResolveBehavior(artifact);
            var output = ComputeOutput(behavior, values);

            if (!OnlyEmitOutputVectorOnChange
                || !_lastVectorOutputByBrain.TryGetValue(brainId, out var previousOutput)
                || previousOutput != output)
            {
                _outputs[brainId].Enqueue(new BasicsRuntimeOutputVector(brainId, tick, new[] { output }));
            }

            _lastVectorOutputByBrain[brainId] = output;
            if (output >= 0.5f)
            {
                _outputEvents[brainId].Enqueue(new BasicsRuntimeOutputEvent(brainId, 0, tick, output));
            }

            return Task.CompletedTask;
        }

        public void ResetOutputBuffer(Guid brainId)
        {
            if (_outputs.TryGetValue(brainId, out var queue))
            {
                queue.Clear();
            }
        }

        public void ResetOutputEventBuffer(Guid brainId)
        {
            if (_outputEvents.TryGetValue(brainId, out var queue))
            {
                queue.Clear();
            }
        }

        public Task<BasicsRuntimeOutputVector?> WaitForOutputVectorAsync(
            Guid brainId,
            ulong afterTickExclusive,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            VectorWaitTimeouts.Add(timeout);
            if (_outputs.TryGetValue(brainId, out var queue))
            {
                while (queue.Count > 0)
                {
                    var output = queue.Dequeue();
                    if (output.TickId > afterTickExclusive)
                    {
                        return Task.FromResult<BasicsRuntimeOutputVector?>(output);
                    }
                }
            }

            return Task.FromResult<BasicsRuntimeOutputVector?>(null);
        }

        public Task<BasicsRuntimeOutputEvent?> WaitForOutputEventAsync(
            Guid brainId,
            ulong afterTickExclusive,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            EventWaitTimeouts.Add(timeout);
            if (_outputEvents.TryGetValue(brainId, out var queue))
            {
                while (queue.Count > 0)
                {
                    var output = queue.Dequeue();
                    if (output.TickId > afterTickExclusive)
                    {
                        return Task.FromResult<BasicsRuntimeOutputEvent?>(output);
                    }
                }
            }

            return Task.FromResult<BasicsRuntimeOutputEvent?>(null);
        }

        public Task<KillBrainViaIOAck?> KillBrainAsync(Guid brainId, string reason, CancellationToken cancellationToken = default)
        {
            _brainDefinitions.Remove(brainId);
            _brainSnapshots.Remove(brainId);
            _outputs.Remove(brainId);
            _outputEvents.Remove(brainId);
            _ticks.Remove(brainId);
            _lastVectorOutputByBrain.Remove(brainId);
            return Task.FromResult<KillBrainViaIOAck?>(new KillBrainViaIOAck { Accepted = true });
        }

        public Task<BrainTerminated?> WaitForBrainTerminatedAsync(
            Guid brainId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult<BrainTerminated?>(new BrainTerminated
            {
                BrainId = brainId.ToProtoUuid(),
                Reason = "basics_evaluation_complete"
            });

        public Task<Nbn.Proto.Io.SetOutputVectorSourceAck?> SetOutputVectorSourceAsync(
            OutputVectorSource outputVectorSource,
            Guid? brainId = null,
            CancellationToken cancellationToken = default)
        {
            SetOutputVectorSourceRequests.Add((brainId ?? Guid.Empty, outputVectorSource));
            return Task.FromResult<Nbn.Proto.Io.SetOutputVectorSourceAck?>(new Nbn.Proto.Io.SetOutputVectorSourceAck
            {
                Success = true,
                OutputVectorSource = outputVectorSource,
                BrainId = brainId.HasValue && brainId.Value != Guid.Empty ? brainId.Value.ToProtoUuid() : null
            });
        }

        public Task<Repro.ReproduceResult?> ReproduceByArtifactsAsync(
            Repro.ReproduceByArtifactsRequest request,
            CancellationToken cancellationToken = default)
        {
            ReproduceCallCount++;
            ReproduceRequests.Add(request.Clone());
            if (ReproduceCallCount == 1)
            {
                SpeciationEpochStartedBeforeFirstReproduce = _epochStarted;
            }

            if (ThrowOnReproduceCallNumber.HasValue && ReproduceCallCount == ThrowOnReproduceCallNumber.Value)
            {
                throw new InvalidOperationException($"reproduce_failed:{ReproduceCallCount}");
            }

            var result = new Repro.ReproduceResult
            {
                RequestedRunCount = request.RunCount == 0 ? 1u : request.RunCount
            };

            for (var runIndex = 0; runIndex < result.RequestedRunCount; runIndex++)
            {
                var behavior = ReproduceCallCount >= 2 ? "and" : "zero";
                var child = CreateArtifactRef(_childIndex++, behavior);
                result.Runs.Add(new Repro.ReproduceRunOutcome
                {
                    RunIndex = (uint)runIndex,
                    ChildDef = child.Clone(),
                    Spawned = false
                });
                if (runIndex == 0)
                {
                    result.ChildDef = child.Clone();
                }
            }

            return Task.FromResult<Repro.ReproduceResult?>(result);
        }

        public Task<SpeciationAssignResponse?> AssignSpeciationAsync(
            SpeciationAssignRequest request,
            CancellationToken cancellationToken = default)
        {
            var requestedSpeciesId = string.IsNullOrWhiteSpace(request.SpeciesId)
                ? "species.default"
                : request.SpeciesId.Trim();
            var response = new SpeciationAssignResponse
            {
                Decision = new SpeciationDecision
                {
                    ApplyMode = SpeciationApplyMode.Commit,
                    CandidateMode = SpeciationCandidateMode.ArtifactRef,
                    Success = true,
                    Committed = true,
                    SpeciesId = requestedSpeciesId,
                    SpeciesDisplayName = string.IsNullOrWhiteSpace(request.SpeciesDisplayName)
                        ? requestedSpeciesId
                        : request.SpeciesDisplayName
                }
            };
            return Task.FromResult<SpeciationAssignResponse?>(response);
        }

        public Task<SpeciationGetConfigResponse?> GetSpeciationConfigAsync(CancellationToken cancellationToken = default)
        {
            GetSpeciationConfigCallCount++;
            return Task.FromResult<SpeciationGetConfigResponse?>(new SpeciationGetConfigResponse
            {
                FailureReason = SpeciationFailureReason.SpeciationFailureNone,
                Config = new SpeciationRuntimeConfig
                {
                    PolicyVersion = "default",
                    ConfigSnapshotJson = "{}",
                    DefaultSpeciesId = "species.default",
                    DefaultSpeciesDisplayName = "Default species",
                    StartupReconcileDecisionReason = "startup_reconcile"
                },
                CurrentEpoch = new SpeciationEpochInfo
                {
                    EpochId = 1
                }
            });
        }

        public Task<SpeciationSetConfigResponse?> SetSpeciationConfigAsync(
            SpeciationRuntimeConfig config,
            bool startNewEpoch,
            CancellationToken cancellationToken = default)
        {
            SetSpeciationConfigCallCount++;
            _epochStarted = startNewEpoch;
            return Task.FromResult<SpeciationSetConfigResponse?>(new SpeciationSetConfigResponse
            {
                FailureReason = SpeciationFailureReason.SpeciationFailureNone,
                Config = config.Clone(),
                CurrentEpoch = new SpeciationEpochInfo
                {
                    EpochId = startNewEpoch ? 2UL : 1UL
                }
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private string ResolveBehavior(ArtifactRef artifact)
        {
            var sha = artifact.ToSha256Hex();
            if (_behaviorByArtifactSha.TryGetValue(sha, out var behavior))
            {
                return behavior;
            }

            _behaviorByArtifactSha[sha] = DefaultBehavior;
            return DefaultBehavior;
        }

        private ArtifactRef CreateArtifactRef(int index, string behavior)
        {
            var template = BasicsSeedTemplateContract.CreateDefault() with
            {
                InitialSeedShapeConstraints = index % 2 == 0
                    ? new BasicsSeedShapeConstraints()
                    : new BasicsSeedShapeConstraints
                    {
                        MinInternalNeuronCount = 2,
                        MinAxonCount = 5
                    }
            };
            var build = BasicsTemplateArtifactBuilder.Build(template);
            var artifact = StoreArtifact(build.Bytes, "application/x-nbn");
            _behaviorByArtifactSha[artifact.ToSha256Hex()] = behavior;
            return artifact;
        }

        private ArtifactRef StoreArtifact(byte[] bytes, string mediaType)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var manifest = _artifactStore.StoreAsync(stream, mediaType).GetAwaiter().GetResult();
            return manifest.ArtifactId.ToHex().ToArtifactRef((ulong)manifest.ByteLength, mediaType, _artifactRoot);
        }

        private void UpdateMaxObservedConcurrentSpawnRequests(int active)
        {
            while (true)
            {
                var current = MaxObservedConcurrentSpawnRequests;
                if (active <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxObservedConcurrentSpawnRequests, active, current) == current)
                {
                    return;
                }
            }
        }

        private static float ComputeOutput(string behavior, IReadOnlyList<float> values)
        {
            if (values.Count < 2)
            {
                return 0f;
            }

            var a = values[0];
            var b = values[1];
            return behavior switch
            {
                "and" => a >= 0.5f && b >= 0.5f ? 1f : 0f,
                "or" => a >= 0.5f || b >= 0.5f ? 1f : 0f,
                "xor" => (a >= 0.5f) ^ (b >= 0.5f) ? 1f : 0f,
                "gt" => a > b ? 1f : 0f,
                "multiplication" => Math.Clamp(a * b, 0f, 1f),
                _ => 0f
            };
        }
    }
}
