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
            Assert.Contains(snapshots, snapshot => snapshot.State == BasicsExecutionState.Running);
            Assert.True(final.AccuracyHistory.Count >= 2);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private sealed class FakeBasicsRuntimeClient : IBasicsRuntimeClient
    {
        private readonly Dictionary<Guid, ArtifactRef> _brainDefinitions = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputVector>> _outputs = new();
        private readonly Dictionary<Guid, ulong> _ticks = new();
        private readonly Dictionary<string, string> _behaviorByArtifactSha = new(StringComparer.OrdinalIgnoreCase);
        private int _childIndex;

        public int ReproduceCallCount { get; private set; }

        public Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default)
            => Task.FromResult<ConnectAck?>(new ConnectAck { ServerName = clientName, ServerTimeMs = 1 });

        public Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<PlacementWorkerInventoryResult?>(null);

        public Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.FromResult<BrainInfo?>(new BrainInfo
            {
                BrainId = brainId.ToProtoUuid(),
                InputWidth = BasicsIoGeometry.InputWidth,
                OutputWidth = BasicsIoGeometry.OutputWidth
            });

        public Task<SpawnBrainViaIOAck?> SpawnBrainAsync(SpawnBrain request, CancellationToken cancellationToken = default)
        {
            var brainId = Guid.NewGuid();
            _brainDefinitions[brainId] = request.BrainDef.Clone();
            _outputs[brainId] = new Queue<BasicsRuntimeOutputVector>();
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
            => Task.CompletedTask;

        public Task UnsubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default)
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
            return Task.CompletedTask;
        }

        public void ResetOutputBuffer(Guid brainId)
        {
            if (_outputs.TryGetValue(brainId, out var queue))
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

        public Task<KillBrainViaIOAck?> KillBrainAsync(Guid brainId, string reason, CancellationToken cancellationToken = default)
        {
            _brainDefinitions.Remove(brainId);
            _outputs.Remove(brainId);
            _ticks.Remove(brainId);
            return Task.FromResult<KillBrainViaIOAck?>(new KillBrainViaIOAck { Accepted = true });
        }

        public Task<Repro.ReproduceResult?> ReproduceByArtifactsAsync(
            Repro.ReproduceByArtifactsRequest request,
            CancellationToken cancellationToken = default)
        {
            ReproduceCallCount++;
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
