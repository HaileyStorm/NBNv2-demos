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
using System.Security.Cryptography;
using Repro = Nbn.Proto.Repro;
using System.Reflection;
using System.Collections.Concurrent;

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
        var session = CreateSession(
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
                    SizingOverrides: new BasicsSizingOverrides(),
                    InitialBrainSeeds: Array.Empty<BasicsInitialBrainSeed>(),
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
                    OutputSamplingPolicy: new BasicsOutputSamplingPolicy(),
                    DiversityPreset: BasicsDiversityPreset.Medium,
                    AdaptiveDiversity: new BasicsAdaptiveDiversityOptions(),
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
    public async Task ExecutionSession_PublishesOrderedStartupStatuses_BeforeGenerationOneEvaluation()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ReproduceDelay = TimeSpan.FromMilliseconds(50)
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);

            var startupStatuses = snapshots
                .Where(snapshot => snapshot.State == BasicsExecutionState.Starting)
                .Select(snapshot => snapshot.StatusText)
                .ToList();
            var firstRunningIndex = snapshots.FindIndex(snapshot => snapshot.State == BasicsExecutionState.Running);
            var firstGenerationEvaluationIndex = snapshots.FindIndex(snapshot =>
                snapshot.State == BasicsExecutionState.Running
                && snapshot.StatusText.Contains("Evaluating generation 1...", StringComparison.Ordinal));
            var initialPopulationReadyIndex = snapshots.FindIndex(snapshot => snapshot.StatusText == "Initial population ready.");

            Assert.True(firstRunningIndex >= 0);
            Assert.True(firstGenerationEvaluationIndex >= 0);
            Assert.True(snapshots.FindIndex(snapshot => snapshot.StatusText == "Starting Basics session...") >= 0);
            Assert.True(snapshots.FindIndex(snapshot => snapshot.StatusText == "Preparing speciation epoch...") > snapshots.FindIndex(snapshot => snapshot.StatusText == "Starting Basics session..."));
            Assert.True(snapshots.FindIndex(snapshot => snapshot.StatusText == "Seeding initial population...") > snapshots.FindIndex(snapshot => snapshot.StatusText == "Preparing speciation epoch..."));
            Assert.True(snapshots.FindIndex(snapshot => snapshot.StatusText == "Generating seed variations...") > snapshots.FindIndex(snapshot => snapshot.StatusText == "Seeding initial population..."));
            Assert.True(snapshots.FindIndex(snapshot => snapshot.StatusText == "Generating seed variations...") < firstRunningIndex);
            Assert.True(initialPopulationReadyIndex >= 0);
            Assert.True(initialPopulationReadyIndex < firstGenerationEvaluationIndex);
            Assert.Contains("Initial population ready.", startupStatuses);
            Assert.Contains(
                snapshots,
                snapshot => snapshot.StatusText == "Generating seed variations..."
                            && snapshot.DetailText.Contains("attempt 1/", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_PublishesUploadedSeedTemplateProgress_BeforeGenerationOneEvaluation()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ReproduceDelay = TimeSpan.FromMilliseconds(50)
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var uploadedSeedBytes = runtimeClient.CreateDefinitionBytes("and");
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential) with
                {
                    InitialBrainSeeds =
                    [
                        new BasicsInitialBrainSeed(
                            DisplayName: "uploaded-and-seed",
                            DefinitionBytes: uploadedSeedBytes,
                            DuplicateForReproduction: false,
                            Complexity: BasicsDefinitionAnalyzer.Analyze(uploadedSeedBytes).Complexity)
                    ]
                },
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);

            var firstRunningIndex = snapshots.FindIndex(snapshot => snapshot.State == BasicsExecutionState.Running);
            var uploadTemplateIndex = snapshots.FindIndex(snapshot =>
                snapshot.StatusText == "Resolving seed templates..."
                && snapshot.DetailText.Contains("uploaded initial brain", StringComparison.OrdinalIgnoreCase));

            Assert.True(uploadTemplateIndex >= 0);
            Assert.True(uploadTemplateIndex < firstRunningIndex);
            Assert.Contains(
                snapshots,
                snapshot => snapshot.StatusText == "Resolving seed templates..."
                            && snapshot.DetailText.Contains("uploaded-and-seed", StringComparison.Ordinal));
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
        var session = CreateSession(runtimeClient);

        try
        {
            var plan = CreatePlan(BasicsOutputObservationMode.VectorPotential, taskPlugin: plugin) with
            {
                Capacity = new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 2,
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
            Assert.Equal(1, runtimeClient.ReproduceCallCount);
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
        var session = CreateSession(runtimeClient);

        try
        {
            var plan = CreatePlan(BasicsOutputObservationMode.VectorPotential, taskPlugin: plugin) with
            {
                Capacity = new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 2,
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
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and"
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.EventedOutput),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.True(runtimeClient.SingleSubscriptionCount > 0);
            Assert.True(runtimeClient.VectorSubscriptionCount > 0);
            Assert.Empty(runtimeClient.SetOutputVectorSourceRequests);
            Assert.NotEmpty(runtimeClient.EventWaitTimeouts);
            Assert.All(runtimeClient.EventWaitTimeouts, timeout => Assert.Equal(TimeSpan.FromSeconds(10), timeout));
            Assert.NotEmpty(runtimeClient.VectorWaitTimeouts);
            Assert.All(runtimeClient.VectorWaitTimeouts, timeout => Assert.Equal(TimeSpan.FromSeconds(10), timeout));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_EventedOutput_WaitsForReadySignalBeforeScoring()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            ReadySignalDelayTicks = 2,
            PreReadyOutputValue = 0f
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.EventedOutput) with
                {
                    OutputSamplingPolicy = new BasicsOutputSamplingPolicy
                    {
                        MaxReadyWindowTicks = 2
                    }
                },
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.Equal(1f, final.BestAccuracy);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_EventedOutput_ForcesPotentialVectorSource_WhenBrainReportsBuffer()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            InitialOutputVectorSource = OutputVectorSource.Buffer
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.EventedOutput),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.Contains(
                runtimeClient.SetOutputVectorSourceRequests,
                static request => request.OutputVectorSource == OutputVectorSource.Potential);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_EventedOutput_FallsBackToReadyLaneInVectorStream_WhenSingleReadyEventIsMissing()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            SuppressReadyOutputEvents = true
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.EventedOutput),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.True(final.BestAccuracy > 0f);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_EventedOutput_ReportsWhenVectorAndReadyStreamsStaySilent()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            SuppressOutputVectors = true,
            SuppressReadyOutputEvents = true
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.EventedOutput),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.Contains(
                "output_timeout_or_width_mismatch:vector_missing",
                final.EvaluationFailureSummary,
                StringComparison.Ordinal);
            Assert.True(runtimeClient.SpawnRequestCount > 2, $"Expected vector-missing failures to retry, observed {runtimeClient.SpawnRequestCount} spawn request(s).");
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_EventedOutput_DoesNotRetryReadyWindowExhaustedBrains()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            ReadySignalDelayTicks = 16,
            PreReadyOutputValue = 0f
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.EventedOutput,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1,
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f
                    }) with
                {
                    OutputSamplingPolicy = new BasicsOutputSamplingPolicy
                    {
                        MaxReadyWindowTicks = 2
                    }
                },
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.Contains("ready_window_exhausted", final.EvaluationFailureSummary, StringComparison.Ordinal);
            Assert.Contains("ready_timeout", final.LatestBatchTiming?.FailureSummary, StringComparison.Ordinal);
            Assert.Equal(2, runtimeClient.SpawnRequestCount);
            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Running
                            && snapshot.StatusText.Contains("Evaluating generation 1...", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("attempt 2/3", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_FailsWhenReadySignalMissesConfiguredWindow()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            ReadySignalDelayTicks = 3,
            PreReadyOutputValue = 0f
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential) with
                {
                    OutputSamplingPolicy = new BasicsOutputSamplingPolicy
                    {
                        MaxReadyWindowTicks = 2
                    }
                },
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.Contains("output_timeout_or_width_mismatch", final.EvaluationFailureSummary, StringComparison.Ordinal);
            Assert.Contains("ready_timeout", final.LatestBatchTiming?.FailureSummary, StringComparison.Ordinal);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_ReportsOutputWidthMismatchSeparately()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            OutputVectorWidthOverride = 1
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.Contains("width_mismatch", final.EvaluationFailureSummary, StringComparison.Ordinal);
            Assert.Contains("output_width_mismatch", final.LatestBatchTiming?.FailureSummary, StringComparison.Ordinal);
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
        var session = CreateSession(runtimeClient);

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
    public async Task ExecutionSession_TracksBestAccuracyFromUploadedInitialBrains()
    {
        var plugin = new MultiplicationTaskPlugin();
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "zero"
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var strongSeedBytes = runtimeClient.CreateDefinitionBytes("multiplication");
            var weakSeedBytes = runtimeClient.CreateDefinitionBytes("zero");
            var strongSeedHash = Convert.ToHexString(SHA256.HashData(strongSeedBytes)).ToLowerInvariant();

            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorBuffer, taskPlugin: plugin) with
                {
                    InitialBrainSeeds =
                    [
                        new BasicsInitialBrainSeed(
                            DisplayName: "strong-multiplication-seed",
                            DefinitionBytes: strongSeedBytes,
                            DuplicateForReproduction: false,
                            Complexity: BasicsDefinitionAnalyzer.Analyze(strongSeedBytes).Complexity),
                        new BasicsInitialBrainSeed(
                            DisplayName: "weak-zero-seed",
                            DefinitionBytes: weakSeedBytes,
                            DuplicateForReproduction: false,
                            Complexity: BasicsDefinitionAnalyzer.Analyze(weakSeedBytes).Complexity)
                    ],
                    Capacity = new BasicsCapacityRecommendation(
                        Source: BasicsCapacitySource.RuntimePlacementInventory,
                        EligibleWorkerCount: 1,
                        RecommendedInitialPopulationCount: 2,
                        RecommendedReproductionRunCount: 1,
                        RecommendedMaxConcurrentBrains: 2,
                        CapacityScore: 1f,
                        EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                        Summary: "test")
                },
                plugin,
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.Equal(1f, final.BestAccuracy);
            Assert.NotNull(final.BestCandidate);
            Assert.Equal(1f, final.BestCandidate.Accuracy);
            Assert.Equal(strongSeedHash, final.BestCandidate.ArtifactSha256);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_PublishesRunningSnapshotWithExportableBestCandidate()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ThrowOnReproduceCallNumber = 2
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);

            var runningSnapshot = Assert.Single(
                snapshots.Where(snapshot =>
                    snapshot.State == BasicsExecutionState.Running
                    && snapshot.StatusText.Contains("Generation 1 evaluated.", StringComparison.Ordinal)));
            Assert.NotNull(runningSnapshot.BestCandidate);
            Assert.Empty(runningSnapshot.BestCandidate.Diagnostics);
            Assert.False(runningSnapshot.BestCandidate.HasRetainedBrain);
            Assert.False(runningSnapshot.BestCandidate.HasSnapshotArtifact);
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
        var session = CreateSession(runtimeClient);

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
        var session = CreateSession(runtimeClient);

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
    public async Task ExecutionSession_StopsAtConfiguredGenerationLimit_AndRetainsBestSoFarCandidate()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    }),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.Equal(1, final.Generation);
            Assert.True(final.BestAccuracy > 0f);
            Assert.True(final.BestFitness > 0f);
            Assert.NotNull(final.BestCandidate);
            Assert.True(final.BestCandidate.HasRetainedBrain);
            Assert.True(final.BestCandidate.HasSnapshotArtifact);
            Assert.Equal(1, runtimeClient.LiveBrainCount);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_RetainsBestSoFarCandidate_WhenLaterFailureOccurs()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ThrowOnReproduceCallNumber = 2
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.Equal(1, final.Generation);
            Assert.True(final.BestAccuracy > 0f);
            Assert.True(final.BestFitness > 0f);
            Assert.NotNull(final.BestCandidate);
            Assert.True(final.BestCandidate.HasRetainedBrain);
            Assert.True(final.BestCandidate.HasSnapshotArtifact);
            Assert.Equal(1, runtimeClient.LiveBrainCount);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_WaitsForPlacementBeforeEvaluatingBrains()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "or",
            RequirePlacementWaitForVisibility = true
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorPotential, taskPlugin: new OrTaskPlugin()),
                new OrTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.NotNull(final.BestCandidate);
            Assert.True(final.BestCandidate.HasRetainedBrain);
            Assert.True(final.BestCandidate.HasSnapshotArtifact);
            Assert.True(runtimeClient.AwaitSpawnPlacementCallCount >= 2);
            Assert.Equal(1, runtimeClient.LiveBrainCount);
            Assert.Equal(1, runtimeClient.SnapshotRequestCount);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task RespawnBestCandidateForExport_WaitsForPlacementBeforeReturningLiveBrain()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            RequirePlacementWaitForVisibility = true
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var method = typeof(BasicsExecutionSession).GetMethod(
                "RespawnBestCandidateForExportAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var bestCandidate = new BasicsExecutionBestCandidateSummary(
                runtimeClient.CreateDefinitionArtifact("and"),
                SnapshotArtifact: null,
                ActiveBrainId: null,
                SpeciesId: "species.default",
                Accuracy: 1f,
                Fitness: 1f,
                Complexity: new BasicsDefinitionComplexitySummary(1, 1, 3),
                ScoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
                {
                    ["task_accuracy"] = 1f,
                    ["truth_table_coverage"] = 1f
                },
                Diagnostics: Array.Empty<string>());

            var taskObject = method!.Invoke(
                session,
                new object[] { new AndTaskPlugin().Contract, bestCandidate, CancellationToken.None });
            var task = Assert.IsAssignableFrom<Task>(taskObject);
            await task;

            var result = task.GetType().GetProperty("Result")!.GetValue(task);
            Assert.NotNull(result);

            var summary = Assert.IsType<BasicsExecutionBestCandidateSummary>(
                result!.GetType().GetField("Item2")!.GetValue(result));
            Assert.True(summary.HasRetainedBrain);
            Assert.True(summary.HasSnapshotArtifact);
            Assert.Equal(1, runtimeClient.AwaitSpawnPlacementCallCount);
            Assert.Equal(1, runtimeClient.LiveBrainCount);
            Assert.Equal(1, runtimeClient.SnapshotRequestCount);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_DoesNotSerializePlacementWaits_BehindSetupConcurrency()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "or",
            RequirePlacementWaitForVisibility = true,
            AwaitPlacementDelay = TimeSpan.FromMilliseconds(50)
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var plan = CreatePlan(
                BasicsOutputObservationMode.VectorPotential,
                new BasicsExecutionStopCriteria
                {
                    MaximumGenerations = 1
                },
                new OrTaskPlugin()) with
            {
                Capacity = new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 4,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 4,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };

            var final = await session.RunAsync(
                plan,
                new OrTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.True(runtimeClient.AwaitSpawnPlacementCallCount >= 4);
            Assert.True(
                runtimeClient.MaxObservedConcurrentPlacementWaits >= 2,
                $"Expected overlapping placement waits, observed {runtimeClient.MaxObservedConcurrentPlacementWaits}.");
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_RetriesPlacementVisibilityTimeouts_UsingBoundedWait()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "or",
            RequirePlacementWaitForVisibility = true,
            AwaitPlacementDelay = TimeSpan.FromMilliseconds(100)
        };
        var session = CreateSession(runtimeClient, spawnPlacementTimeout: TimeSpan.FromMilliseconds(40));

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    },
                    new OrTaskPlugin()),
                new OrTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.NotEqual(BasicsExecutionState.Succeeded, final.State);
            Assert.True(runtimeClient.SpawnRequestCount >= 3, $"Expected retried placement timeouts, observed {runtimeClient.SpawnRequestCount} spawn request(s).");
            Assert.Contains(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Running
                            && snapshot.StatusText.Contains("Evaluating generation 1...", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("attempt 2/3", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_KeepsAtLeastTwoBrains_WhenReproductionProducesNoChildren()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ReturnNoChildrenStartingAtReproduceCallNumber = 2
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 2
                    }),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.Equal(2, final.Generation);
            Assert.True(final.PopulationCount >= 2);
            Assert.Contains(
                snapshots,
                snapshot => snapshot.StatusText.Contains("Generation 2 evaluated.", StringComparison.Ordinal)
                            && snapshot.PopulationCount >= 2);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_PreservesAccuracyAndFitnessHistories_InEvaluationProgressSnapshots()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "zero"
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f,
                        MaximumGenerations = 2
                    }),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);

            var generationOneSummary = Assert.Single(
                snapshots.Where(snapshot =>
                    snapshot.State == BasicsExecutionState.Running
                    && snapshot.StatusText.Contains("Generation 1 evaluated.", StringComparison.Ordinal)));
            var generationTwoEvaluationSnapshot = snapshots.FirstOrDefault(snapshot =>
                snapshot.State == BasicsExecutionState.Running
                && snapshot.StatusText.Contains("Evaluating generation 2...", StringComparison.Ordinal)
                && snapshot.OffspringAccuracyHistory.Count > 0);

            Assert.True(generationOneSummary.OffspringBestAccuracy > 0f);
            Assert.True(generationOneSummary.OffspringBestFitness > generationOneSummary.OffspringBestAccuracy);
            Assert.NotNull(generationTwoEvaluationSnapshot);
            Assert.Equal(
                generationOneSummary.OffspringBestAccuracy,
                generationTwoEvaluationSnapshot!.OffspringAccuracyHistory[^1]);
            Assert.Equal(
                generationOneSummary.BestAccuracy,
                generationTwoEvaluationSnapshot.AccuracyHistory[^1]);
            Assert.Equal(
                generationOneSummary.OffspringBestFitness,
                generationTwoEvaluationSnapshot.OffspringFitnessHistory[^1]);
            Assert.Equal(
                generationOneSummary.BestFitness,
                generationTwoEvaluationSnapshot.BestFitnessHistory[^1]);
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
        var session = CreateSession(runtimeClient);

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
                    SizingOverrides: new BasicsSizingOverrides(),
                    InitialBrainSeeds: Array.Empty<BasicsInitialBrainSeed>(),
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
                    OutputSamplingPolicy: new BasicsOutputSamplingPolicy(),
                    DiversityPreset: BasicsDiversityPreset.Medium,
                    AdaptiveDiversity: new BasicsAdaptiveDiversityOptions(),
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

    [Fact]
    public async Task ExecutionSession_PacesSpawnRequests_PerEligibleWorker()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = CreateSession(runtimeClient, minimumSpawnRequestInterval: TimeSpan.FromMilliseconds(75));

        try
        {
            var final = await session.RunAsync(
                new BasicsEnvironmentPlan(
                    SelectedTask: new AndTaskPlugin().Contract,
                    SeedTemplate: BasicsSeedTemplateContract.CreateDefault(),
                    SizingOverrides: new BasicsSizingOverrides(),
                    InitialBrainSeeds: Array.Empty<BasicsInitialBrainSeed>(),
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
                    OutputSamplingPolicy: new BasicsOutputSamplingPolicy(),
                    DiversityPreset: BasicsDiversityPreset.Medium,
                    AdaptiveDiversity: new BasicsAdaptiveDiversityOptions(),
                    Reproduction: BasicsReproductionPolicy.CreateDefault(),
                    Scheduling: BasicsReproductionSchedulingPolicy.Default,
                    Metrics: BasicsMetricsContract.Default,
                    StopCriteria: new BasicsExecutionStopCriteria(),
                    PlannedAtUtc: DateTimeOffset.UtcNow),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            var spawnStarts = runtimeClient.SpawnRequestStartedAtUtc.ToArray();
            Assert.True(spawnStarts.Length >= 4);
            var deltas = spawnStarts
                .Zip(spawnStarts.Skip(1), (left, right) => right - left)
                .Take(3)
                .ToArray();
            Assert.NotEmpty(deltas);
            Assert.All(deltas, delta => Assert.True(delta >= TimeSpan.FromMilliseconds(60), $"Observed spawn delta {delta.TotalMilliseconds:0.###}ms was below the paced floor."));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_SkipsDuplicateOffspringDefinitions_DuringBreeding()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ReuseSingleChildArtifactOnReproduce = true
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                new BasicsEnvironmentPlan(
                    SelectedTask: new AndTaskPlugin().Contract,
                    SeedTemplate: BasicsSeedTemplateContract.CreateDefault(),
                    SizingOverrides: new BasicsSizingOverrides(),
                    InitialBrainSeeds: Array.Empty<BasicsInitialBrainSeed>(),
                    Capacity: new BasicsCapacityRecommendation(
                        Source: BasicsCapacitySource.RuntimePlacementInventory,
                        EligibleWorkerCount: 1,
                        RecommendedInitialPopulationCount: 4,
                        RecommendedReproductionRunCount: 4,
                        RecommendedMaxConcurrentBrains: 1,
                        CapacityScore: 1f,
                        EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                        Summary: "test"),
                    OutputObservationMode: BasicsOutputObservationMode.VectorPotential,
                    OutputSamplingPolicy: new BasicsOutputSamplingPolicy(),
                    DiversityPreset: BasicsDiversityPreset.Medium,
                    AdaptiveDiversity: new BasicsAdaptiveDiversityOptions(),
                    Reproduction: BasicsReproductionPolicy.CreateDefault(),
                    Scheduling: BasicsReproductionSchedulingPolicy.Default,
                    Metrics: BasicsMetricsContract.Default,
                    StopCriteria: new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 2,
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f
                    },
                    PlannedAtUtc: DateTimeOffset.UtcNow),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.True(runtimeClient.ReproduceCallCount > 1, $"Expected duplicate offspring to force additional breeding attempts, observed {runtimeClient.ReproduceCallCount} reproduce call(s).");
            Assert.Contains(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Running
                            && snapshot.StatusText.Contains("Breeding generation 2...", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("Skipped", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("duplicate child definition", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_RetriesTransientSpawnFailures()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            TransientSpawnFailureCount = 1,
            SpawnDelay = TimeSpan.FromMilliseconds(300)
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    }),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.True(final.BestAccuracy > 0f);
            Assert.True(runtimeClient.SpawnRequestCount >= 3, $"Expected at least one retried spawn, observed {runtimeClient.SpawnRequestCount} spawn request(s).");
            Assert.Contains(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Running
                            && snapshot.StatusText.Contains("Evaluating generation 1...", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("brain 1/1", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("attempt 1/3", StringComparison.Ordinal));
            Assert.Contains(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Running
                            && snapshot.StatusText.Contains("Evaluating generation 1...", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("brain 1/1", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("attempt 2/3", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_DisablesAdaptiveRuntimeFeatures_ForEvaluationBrains()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    }),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.Contains(runtimeClient.SetCostEnergyEnabledRequests, request => request.BrainId != Guid.Empty && !request.Enabled);
            Assert.Contains(runtimeClient.SetPlasticityEnabledRequests, request => request.BrainId != Guid.Empty && !request.Enabled);
            Assert.Contains(runtimeClient.SetHomeostasisEnabledRequests, request => request.BrainId != Guid.Empty && !request.Enabled);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_ResetsBrainRuntimeState_BeforeEachSample()
    {
        var runtimeClient = new FakeBasicsRuntimeClient();
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    }),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.True(runtimeClient.ResetBrainRuntimeStateRequests.Count >= 4);
            Assert.All(
                runtimeClient.ResetBrainRuntimeStateRequests,
                request =>
                {
                    Assert.NotEqual(Guid.Empty, request.BrainId);
                    Assert.True(request.ResetBuffer);
                    Assert.True(request.ResetAccumulator);
                });
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
            SizingOverrides: new BasicsSizingOverrides(),
            InitialBrainSeeds: Array.Empty<BasicsInitialBrainSeed>(),
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
                OutputSamplingPolicy: new BasicsOutputSamplingPolicy(),
                DiversityPreset: BasicsDiversityPreset.Medium,
                AdaptiveDiversity: new BasicsAdaptiveDiversityOptions(),
                Reproduction: BasicsReproductionPolicy.CreateDefault(),
                Scheduling: BasicsReproductionSchedulingPolicy.Default,
                Metrics: BasicsMetricsContract.Default,
            StopCriteria: stopCriteria ?? new BasicsExecutionStopCriteria(),
            PlannedAtUtc: DateTimeOffset.UtcNow);
    }

    private static BasicsExecutionSession CreateSession(
        FakeBasicsRuntimeClient runtimeClient,
        BasicsTemplatePublishingOptions? publishingOptions = null,
        TimeSpan? minimumSpawnRequestInterval = null,
        TimeSpan? spawnPlacementTimeout = null)
        => new(
            runtimeClient,
            publishingOptions ?? new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" },
            minimumSpawnRequestInterval ?? TimeSpan.FromMilliseconds(1),
            spawnPlacementTimeout);

    private sealed class FakeBasicsRuntimeClient : IBasicsRuntimeClient
    {
        private readonly Dictionary<Guid, ArtifactRef> _brainDefinitions = new();
        private readonly Dictionary<Guid, ArtifactRef> _brainSnapshots = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputVector>> _outputs = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputEvent>> _outputEvents = new();
        private readonly Dictionary<Guid, ulong> _ticks = new();
        private readonly Dictionary<Guid, ArtifactRef> _pendingBrainDefinitions = new();
        private readonly Dictionary<string, string> _behaviorByArtifactSha = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _artifactRoot = Path.Combine(Path.GetTempPath(), "nbn-basics-tests", Guid.NewGuid().ToString("N"));
        private readonly LocalArtifactStore _artifactStore;
        private ArtifactRef? _reusedReproduceChildArtifact;
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
        public int SpawnRequestCount { get; private set; }
        public int? ThrowOnReproduceCallNumber { get; init; }
        public int? ReturnNoChildrenOnReproduceCallNumber { get; init; }
        public int? ReturnNoChildrenStartingAtReproduceCallNumber { get; init; }
        public int TransientSpawnFailureCount { get; set; }
        public TimeSpan SpawnDelay { get; init; }
        public TimeSpan ReproduceDelay { get; init; }
        public string DefaultBehavior { get; init; } = "zero";
        public bool OnlyEmitOutputVectorOnChange { get; init; }
        public int ReadySignalDelayTicks { get; init; } = 1;
        public float PreReadyOutputValue { get; init; }
        public bool SuppressOutputVectors { get; init; }
        public bool SuppressReadyOutputEvents { get; init; }
        public bool RequirePlacementWaitForVisibility { get; init; }
        public TimeSpan AwaitPlacementDelay { get; init; }
        public int? OutputVectorWidthOverride { get; init; }
        public bool ReuseSingleChildArtifactOnReproduce { get; init; }
        public OutputVectorSource InitialOutputVectorSource { get; init; } = OutputVectorSource.Potential;
        public int MaxObservedConcurrentSpawnRequests => _maxObservedConcurrentSpawnRequests;
        public int MaxObservedConcurrentPlacementWaits => _maxObservedConcurrentPlacementWaits;
        public int AwaitSpawnPlacementCallCount { get; private set; }
        public List<(Guid BrainId, OutputVectorSource OutputVectorSource)> SetOutputVectorSourceRequests { get; } = new();
        public List<(Guid BrainId, bool Enabled)> SetCostEnergyEnabledRequests { get; } = new();
        public List<(Guid BrainId, bool Enabled)> SetPlasticityEnabledRequests { get; } = new();
        public List<(Guid BrainId, bool Enabled)> SetHomeostasisEnabledRequests { get; } = new();
        public List<(Guid BrainId, bool ResetBuffer, bool ResetAccumulator)> ResetBrainRuntimeStateRequests { get; } = new();
        public List<Repro.ReproduceByArtifactsRequest> ReproduceRequests { get; } = new();
        public List<TimeSpan> VectorWaitTimeouts { get; } = new();
        public List<TimeSpan> EventWaitTimeouts { get; } = new();
        public ConcurrentQueue<DateTimeOffset> SpawnRequestStartedAtUtc { get; } = new();
        private int _activeSpawnRequests;
        private int _maxObservedConcurrentSpawnRequests;
        private int _activePlacementWaits;
        private int _maxObservedConcurrentPlacementWaits;
        private readonly Dictionary<Guid, (float Value, float Ready)> _lastVectorOutputByBrain = new();

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
                    OutputWidth = BasicsIoGeometry.OutputWidth,
                    OutputVectorSource = InitialOutputVectorSource
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
                SpawnRequestCount++;
                SpawnRequestStartedAtUtc.Enqueue(DateTimeOffset.UtcNow);
                if (SpawnDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SpawnDelay, cancellationToken);
                }

                if (TransientSpawnFailureCount > 0)
                {
                    TransientSpawnFailureCount--;
                    return new SpawnBrainViaIOAck
                    {
                        Ack = new SpawnBrainAck
                        {
                            BrainId = Guid.NewGuid().ToProtoUuid(),
                            AcceptedForPlacement = false,
                            PlacementReady = false,
                            FailureReasonCode = "spawn_request_failed",
                            FailureMessage = "transient_test_failure"
                        }
                    };
                }

                var brainId = Guid.NewGuid();
                if (RequirePlacementWaitForVisibility)
                {
                    _pendingBrainDefinitions[brainId] = request.BrainDef.Clone();
                }
                else
                {
                    ActivateBrain(brainId, request.BrainDef);
                }

                return new SpawnBrainViaIOAck
                {
                    Ack = new SpawnBrainAck
                    {
                        BrainId = brainId.ToProtoUuid(),
                        AcceptedForPlacement = true,
                        PlacementReady = !RequirePlacementWaitForVisibility
                    }
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeSpawnRequests);
            }
        }

        public async Task<AwaitSpawnPlacementViaIOAck?> AwaitSpawnPlacementAsync(
            Guid brainId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AwaitSpawnPlacementCallCount++;
            var active = Interlocked.Increment(ref _activePlacementWaits);
            UpdateMaxObservedConcurrentPlacementWaits(active);
            try
            {
                if (AwaitPlacementDelay > TimeSpan.Zero)
                {
                    if (timeout > TimeSpan.Zero && AwaitPlacementDelay > timeout)
                    {
                        await Task.Delay(timeout, cancellationToken);
                        return new AwaitSpawnPlacementViaIOAck
                        {
                            Ack = new SpawnBrainAck
                            {
                                BrainId = brainId.ToProtoUuid(),
                                AcceptedForPlacement = true,
                                PlacementReady = false,
                                FailureReasonCode = "spawn_request_canceled",
                                FailureMessage = "placement visibility timed out"
                            },
                            FailureReasonCode = "spawn_request_canceled",
                            FailureMessage = "placement visibility timed out"
                        };
                    }

                    await Task.Delay(AwaitPlacementDelay, cancellationToken);
                }

                if (_pendingBrainDefinitions.Remove(brainId, out var definition))
                {
                    ActivateBrain(brainId, definition);
                }

                var placed = _brainDefinitions.ContainsKey(brainId);
                return new AwaitSpawnPlacementViaIOAck
                {
                    Ack = new SpawnBrainAck
                    {
                        BrainId = brainId.ToProtoUuid(),
                        AcceptedForPlacement = placed,
                        PlacementReady = placed,
                        FailureReasonCode = placed ? string.Empty : "spawn_unknown_brain",
                        FailureMessage = placed ? string.Empty : "Pending spawn was not found."
                    },
                    FailureReasonCode = placed ? string.Empty : "spawn_unknown_brain",
                    FailureMessage = placed ? string.Empty : "Pending spawn was not found."
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activePlacementWaits);
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
            var behavior = ResolveBehavior(artifact);
            var finalOutput = ComputeOutput(behavior, values);
            var readyDelayTicks = Math.Max(1, ReadySignalDelayTicks);

            for (var offset = 1; offset <= readyDelayTicks; offset++)
            {
                var tick = ++_ticks[brainId];
                var ready = offset == readyDelayTicks ? 1f : 0f;
                var value = offset == readyDelayTicks ? finalOutput : PreReadyOutputValue;
                var vector = new[] { value, ready };
                if (OutputVectorWidthOverride is int outputVectorWidth)
                {
                    vector = vector.Take(Math.Max(0, Math.Min(outputVectorWidth, vector.Length))).ToArray();
                }
                if (!SuppressOutputVectors
                    && (!OnlyEmitOutputVectorOnChange
                        || !_lastVectorOutputByBrain.TryGetValue(brainId, out var previousOutput)
                        || previousOutput.Value != value
                        || previousOutput.Ready != ready
                        || ready >= 0.5f))
                {
                    _outputs[brainId].Enqueue(new BasicsRuntimeOutputVector(brainId, tick, vector));
                }

                _lastVectorOutputByBrain[brainId] = (value, ready);
                if (value >= 0.5f)
                {
                    _outputEvents[brainId].Enqueue(new BasicsRuntimeOutputEvent(brainId, 0, tick, value));
                }

                if (ready >= 0.5f && !SuppressReadyOutputEvents)
                {
                    _outputEvents[brainId].Enqueue(new BasicsRuntimeOutputEvent(brainId, 1, tick, ready));
                }
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
            uint? outputIndex = null,
            CancellationToken cancellationToken = default)
        {
            EventWaitTimeouts.Add(timeout);
            if (_outputEvents.TryGetValue(brainId, out var queue))
            {
                while (queue.Count > 0)
                {
                    var output = queue.Dequeue();
                    if (output.TickId > afterTickExclusive
                        && (!outputIndex.HasValue || output.OutputIndex == outputIndex.Value))
                    {
                        return Task.FromResult<BasicsRuntimeOutputEvent?>(output);
                    }
                }
            }

            return DelayNullOutputEventAsync(timeout, cancellationToken);
        }

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
        {
            _brainDefinitions.Remove(brainId);
            _pendingBrainDefinitions.Remove(brainId);
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

        public Task<IoCommandAck?> SetCostEnergyEnabledAsync(
            Guid brainId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            SetCostEnergyEnabledRequests.Add((brainId, enabled));
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "set_cost_energy",
                Success = true,
                Message = "applied"
            });
        }

        public Task<IoCommandAck?> SetPlasticityEnabledAsync(
            Guid brainId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            SetPlasticityEnabledRequests.Add((brainId, enabled));
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "set_plasticity",
                Success = true,
                Message = "applied"
            });
        }

        public Task<IoCommandAck?> SetHomeostasisEnabledAsync(
            Guid brainId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            SetHomeostasisEnabledRequests.Add((brainId, enabled));
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "set_homeostasis",
                Success = true,
                Message = "applied"
            });
        }

        public Task<IoCommandAck?> ResetBrainRuntimeStateAsync(
            Guid brainId,
            bool resetBuffer,
            bool resetAccumulator,
            CancellationToken cancellationToken = default)
        {
            ResetBrainRuntimeStateRequests.Add((brainId, resetBuffer, resetAccumulator));
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "reset_brain_runtime_state",
                Success = true,
                Message = "applied"
            });
        }

        public async Task<Repro.ReproduceResult?> ReproduceByArtifactsAsync(
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

            if (ReproduceDelay > TimeSpan.Zero)
            {
                await Task.Delay(ReproduceDelay, cancellationToken);
            }

            var result = new Repro.ReproduceResult
            {
                RequestedRunCount = request.RunCount == 0 ? 1u : request.RunCount
            };

            if (ReturnNoChildrenOnReproduceCallNumber.HasValue && ReproduceCallCount == ReturnNoChildrenOnReproduceCallNumber.Value)
            {
                return result;
            }

            if (ReturnNoChildrenStartingAtReproduceCallNumber.HasValue && ReproduceCallCount >= ReturnNoChildrenStartingAtReproduceCallNumber.Value)
            {
                return result;
            }

            for (var runIndex = 0; runIndex < result.RequestedRunCount; runIndex++)
            {
                var behavior = ReproduceCallCount >= 2 ? "and" : DefaultBehavior;
                var child = ReuseSingleChildArtifactOnReproduce
                    ? (_reusedReproduceChildArtifact ??= CreateStoredDefinition(_childIndex++, behavior).Artifact).Clone()
                    : CreateStoredDefinition(_childIndex++, behavior).Artifact;
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

            return result;
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

        public ArtifactRef CreateDefinitionArtifact(string behavior)
            => CreateStoredDefinition(_childIndex++, behavior).Artifact;

        public byte[] CreateDefinitionBytes(string behavior)
            => CreateStoredDefinition(_childIndex++, behavior).Bytes.ToArray();

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

        private (byte[] Bytes, ArtifactRef Artifact) CreateStoredDefinition(int index, string behavior)
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
            return (build.Bytes, artifact);
        }

        private ArtifactRef StoreArtifact(byte[] bytes, string mediaType)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var manifest = _artifactStore.StoreAsync(stream, mediaType).GetAwaiter().GetResult();
            return manifest.ArtifactId.ToHex().ToArtifactRef((ulong)manifest.ByteLength, mediaType, _artifactRoot);
        }

        private void ActivateBrain(Guid brainId, ArtifactRef definition)
        {
            _brainDefinitions[brainId] = definition.Clone();
            _outputs[brainId] = new Queue<BasicsRuntimeOutputVector>();
            _outputEvents[brainId] = new Queue<BasicsRuntimeOutputEvent>();
            _ticks[brainId] = 0;
            _lastVectorOutputByBrain.Remove(brainId);
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

        private void UpdateMaxObservedConcurrentPlacementWaits(int active)
        {
            while (true)
            {
                var current = MaxObservedConcurrentPlacementWaits;
                if (active <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxObservedConcurrentPlacementWaits, active, current) == current)
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

        private static async Task<BasicsRuntimeOutputEvent?> DelayNullOutputEventAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (timeout > TimeSpan.Zero)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
            }

            return null;
        }
    }
}
