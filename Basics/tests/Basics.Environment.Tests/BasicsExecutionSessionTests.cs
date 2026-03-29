using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;
using Nbn.Proto;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Proto.Speciation;
using Nbn.Shared;
using Nbn.Shared.Format;
using Nbn.Shared.Validation;
using Repro = Nbn.Proto.Repro;

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
            Assert.Contains(
                runtimeClient.SetOutputVectorSourceRequests,
                static request => request.BrainId != Guid.Empty && request.OutputVectorSource == OutputVectorSource.Potential);
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

    private static BasicsEnvironmentPlan CreatePlan(BasicsOutputObservationMode outputObservationMode)
        => new(
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
            OutputObservationMode: outputObservationMode,
            Reproduction: BasicsReproductionPolicy.CreateDefault(),
            Scheduling: BasicsReproductionSchedulingPolicy.Default,
            Metrics: BasicsMetricsContract.Default,
            PlannedAtUtc: DateTimeOffset.UtcNow);

    private sealed class FakeBasicsRuntimeClient : IBasicsRuntimeClient
    {
        private readonly Dictionary<Guid, ArtifactRef> _brainDefinitions = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputVector>> _outputs = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputEvent>> _outputEvents = new();
        private readonly Dictionary<Guid, ulong> _ticks = new();
        private readonly Dictionary<string, string> _behaviorByArtifactSha = new(StringComparer.OrdinalIgnoreCase);
        private int _childIndex;
        private bool _epochStarted;

        public int ReproduceCallCount { get; private set; }
        public int GetSpeciationConfigCallCount { get; private set; }
        public int SetSpeciationConfigCallCount { get; private set; }
        public bool SpeciationEpochStartedBeforeFirstReproduce { get; private set; }
        public int VectorSubscriptionCount { get; private set; }
        public int SingleSubscriptionCount { get; private set; }
        public int? ThrowOnReproduceCallNumber { get; init; }
        public List<(Guid BrainId, OutputVectorSource OutputVectorSource)> SetOutputVectorSourceRequests { get; } = new();
        public List<Repro.ReproduceByArtifactsRequest> ReproduceRequests { get; } = new();

        public Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default)
            => Task.FromResult<ConnectAck?>(new ConnectAck { ServerName = clientName, ServerTimeMs = 1 });

        public Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<PlacementWorkerInventoryResult?>(null);

        public Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.FromResult<BrainInfo?>(_brainDefinitions.ContainsKey(brainId)
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

        public Task<SpawnBrainViaIOAck?> SpawnBrainAsync(SpawnBrain request, CancellationToken cancellationToken = default)
        {
            var brainId = Guid.NewGuid();
            _brainDefinitions[brainId] = request.BrainDef.Clone();
            _outputs[brainId] = new Queue<BasicsRuntimeOutputVector>();
            _outputEvents[brainId] = new Queue<BasicsRuntimeOutputEvent>();
            _ticks[brainId] = 0;
            return Task.FromResult<SpawnBrainViaIOAck?>(new SpawnBrainViaIOAck
            {
                Ack = new SpawnBrainAck
                {
                    BrainId = brainId.ToProtoUuid()
                }
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
            var output = behavior switch
            {
                "and" => values.Count >= 2 && values[0] >= 0.5f && values[1] >= 0.5f ? 1f : 0f,
                _ => 0f
            };

            _outputs[brainId].Enqueue(new BasicsRuntimeOutputVector(brainId, tick, new[] { output }));
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
            _outputs.Remove(brainId);
            _outputEvents.Remove(brainId);
            _ticks.Remove(brainId);
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

            _behaviorByArtifactSha[sha] = "zero";
            return "zero";
        }

        private ArtifactRef CreateArtifactRef(int index, string behavior)
        {
            var hexChar = index % 2 == 0 ? 'a' : 'b';
            var sha = new string(hexChar, 63) + ((char)('0' + (index % 10)));
            var artifact = sha.ToArtifactRef(256, "application/x-nbn", $"http://fake-store/{index}");
            _behaviorByArtifactSha[artifact.ToSha256Hex()] = behavior;
            return artifact;
        }
    }
}
