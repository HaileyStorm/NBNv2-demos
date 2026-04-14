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
using System.Text.Json;
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
            var seedChildAssignment = Assert.Single(
                runtimeClient.SpeciationAssignRequests,
                request => string.Equals(request.DecisionReason, "basics_seed_child", StringComparison.Ordinal));
            AssertSpeciationMetadataIncludesLineageScores(seedChildAssignment.DecisionMetadataJson);
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
            Assert.Equal(2, runtimeClient.VectorWaitTimeouts.Count(timeout => timeout > TimeSpan.FromSeconds(10)));
            Assert.All(
                runtimeClient.VectorWaitTimeouts.Where(timeout => timeout <= TimeSpan.FromSeconds(10)),
                timeout => Assert.Equal(TimeSpan.FromSeconds(10), timeout));
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
    public async Task ExecutionSession_EventedOutput_FailsWhenReadyEventIsMissing()
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

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.Contains("output_timeout_or_width_mismatch", final.EvaluationFailureSummary, StringComparison.Ordinal);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_EventedOutput_PrefersReadyEventOverEarlierReadyLaneFallback()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            ReadySignalDelayTicks = 2,
            PreReadyOutputValue = 0f,
            PreReadyReadyValue = 1f,
            SuppressPreReadyReadyOutputEvents = true
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
    public void ExecutionSession_EventedOutput_UsesEarliestReadyTick_WhenMultipleReadyTicksAreAvailable()
    {
        var helper = typeof(BasicsExecutionSession).GetMethod(
            "TryResolveEarliestReadyObservation",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(helper);

        var vectorsByTick = new Dictionary<ulong, BasicsRuntimeOutputVector>
        {
            [7] = new(Guid.NewGuid(), 7, new[] { 0.7f, 1f }),
            [5] = new(Guid.NewGuid(), 5, new[] { 0.5f, 1f })
        };
        var readyTicks = new HashSet<ulong> { 7, 5 };
        var args = new object?[] { vectorsByTick, readyTicks, null };

        var resolved = Assert.IsType<bool>(helper!.Invoke(null, args));
        Assert.True(resolved);
        var observation = Assert.IsType<BasicsTaskObservation>(args[2]);
        Assert.Equal(5UL, observation.TickId);
        Assert.Equal(0.5f, observation.OutputValue);
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
                "vector_missing",
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
    public async Task ExecutionSession_PrimesOutputPath_BeforeScoringFirstSample()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            ResumeOutputActivationDelay = TimeSpan.FromSeconds(12)
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.EventedOutput,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1,
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f
                    }),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(45)).Token);

            Assert.True(
                final.State is BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded,
                $"Expected a clean terminal state, observed {final.State}.");
            Assert.Equal(0, final.EvaluationFailureCount);
            Assert.True(final.BestAccuracy > 0f);
            Assert.Contains(
                runtimeClient.VectorWaitTimeouts,
                static timeout => timeout > TimeSpan.FromSeconds(10));
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
    public async Task ExecutionSession_PreservesUploadedSeedTraceability_InGenerationOneAndBestCandidate()
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
            var weakSeedHash = Convert.ToHexString(SHA256.HashData(weakSeedBytes)).ToLowerInvariant();

            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorBuffer, taskPlugin: plugin) with
                {
                    InitialBrainSeeds =
                    [
                        new BasicsInitialBrainSeed(
                            DisplayName: "strong-multiplication-seed",
                            DefinitionBytes: strongSeedBytes,
                            DuplicateForReproduction: false,
                            Complexity: BasicsDefinitionAnalyzer.Analyze(strongSeedBytes).Complexity)
                        {
                            ContentHash = strongSeedHash
                        },
                        new BasicsInitialBrainSeed(
                            DisplayName: "weak-zero-seed",
                            DefinitionBytes: weakSeedBytes,
                            DuplicateForReproduction: false,
                            Complexity: BasicsDefinitionAnalyzer.Analyze(weakSeedBytes).Complexity)
                        {
                            ContentHash = weakSeedHash
                        }
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
            Assert.NotNull(final.BestCandidate);
            Assert.NotEmpty(final.BootstrapCandidateTraces);

            var strongTrace = Assert.Single(
                final.BootstrapCandidateTraces.Where(trace =>
                    trace.Origin.Kind == BasicsBootstrapOriginKind.UploadedExactCopy
                    && string.Equals(trace.Origin.SourceDisplayName, "strong-multiplication-seed", StringComparison.Ordinal)));
            Assert.Equal(strongSeedHash, strongTrace.Origin.SourceContentHash);
            Assert.Equal(final.BestCandidate!.ArtifactSha256, strongTrace.ArtifactSha256);
            Assert.NotNull(final.BestCandidate.BootstrapOrigin);
            Assert.Equal(BasicsBootstrapOriginKind.UploadedExactCopy, final.BestCandidate.BootstrapOrigin!.Kind);
            Assert.Equal("strong-multiplication-seed", final.BestCandidate.BootstrapOrigin.SourceDisplayName);
            Assert.Equal(strongSeedHash, final.BestCandidate.BootstrapOrigin.SourceContentHash);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_UsesUploadedSeedSnapshot_ForExactCopySpawn()
    {
        var plugin = new MultiplicationTaskPlugin();
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "multiplication"
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var seedBytes = runtimeClient.CreateDefinitionBytes("multiplication");
            var snapshotBytes = new byte[] { 0x4E, 0x42, 0x4E, 0x53, 0x01, 0x00, 0x00, 0x00 };

            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.VectorBuffer, taskPlugin: plugin) with
                {
                    InitialBrainSeeds =
                    [
                        new BasicsInitialBrainSeed(
                            DisplayName: "seed-with-snapshot",
                            DefinitionBytes: seedBytes,
                            DuplicateForReproduction: false,
                            Complexity: BasicsDefinitionAnalyzer.Analyze(seedBytes).Complexity)
                        {
                            SnapshotBytes = snapshotBytes
                        }
                    ],
                    Capacity = new BasicsCapacityRecommendation(
                        Source: BasicsCapacitySource.RuntimePlacementInventory,
                        EligibleWorkerCount: 1,
                        RecommendedInitialPopulationCount: 1,
                        RecommendedReproductionRunCount: 1,
                        RecommendedMaxConcurrentBrains: 1,
                        CapacityScore: 1f,
                        EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                        Summary: "test")
                },
                plugin,
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            var snapshotAwareSpawn = Assert.Single(runtimeClient.SpawnRequests.Where(static request => request.LastSnapshot is not null));
            Assert.Equal("application/x-nbs", snapshotAwareSpawn.LastSnapshot.MediaType);
            Assert.True(snapshotAwareSpawn.LastSnapshot.TryToSha256Bytes(out _));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task SelectBestCandidateSummary_PrefersHigherFitness_WhenAccuracyTies()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient();
        var method = typeof(BasicsExecutionSession).GetMethod("SelectBestCandidateSummary", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var baseline = CreateBestCandidateSummary(runtimeClient, "and", accuracy: 0.75f, fitness: 0.40f);
        var challenger = CreateBestCandidateSummary(runtimeClient, "or", accuracy: 0.75f, fitness: 0.55f);

        var selected = Assert.IsType<BasicsExecutionBestCandidateSummary>(
            method!.Invoke(null, new object?[] { baseline, challenger }));

        Assert.Equal(challenger.ArtifactSha256, selected.ArtifactSha256);
    }

    [Fact]
    public async Task SelectBestCandidateSummary_PrefersHigherAccuracy_OverHigherFitness()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient();
        var method = typeof(BasicsExecutionSession).GetMethod("SelectBestCandidateSummary", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var baseline = CreateBestCandidateSummary(runtimeClient, "and", accuracy: 0.75f, fitness: 0.40f);
        var challenger = CreateBestCandidateSummary(runtimeClient, "or", accuracy: 0.5f, fitness: 0.80f);

        var selected = Assert.IsType<BasicsExecutionBestCandidateSummary>(
            method!.Invoke(null, new object?[] { baseline, challenger }));

        Assert.Equal(baseline.ArtifactSha256, selected.ArtifactSha256);
    }

    [Fact]
    public async Task SelectBestCandidateSummary_PrefersHigherBalancedAccuracy_ForMultiplicationCandidates()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient();
        var method = typeof(BasicsExecutionSession).GetMethod("SelectBestCandidateSummary", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var baseline = CreateBestCandidateSummary(
            runtimeClient,
            "multiplication",
            accuracy: 0.75f,
            fitness: 0.70f,
            scoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["task_accuracy"] = 0.75f,
                ["balanced_tolerance_accuracy"] = 0.61f,
                ["edge_tolerance_accuracy"] = 0.44f,
                ["interior_tolerance_accuracy"] = 0.70f
            });
        var challenger = CreateBestCandidateSummary(
            runtimeClient,
            "multiplication",
            accuracy: 0.72f,
            fitness: 0.71f,
            scoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["task_accuracy"] = 0.72f,
                ["balanced_tolerance_accuracy"] = 0.80f,
                ["edge_tolerance_accuracy"] = 0.56f,
                ["interior_tolerance_accuracy"] = 0.89f
            });

        var selected = Assert.IsType<BasicsExecutionBestCandidateSummary>(
            method!.Invoke(null, new object?[] { baseline, challenger }));

        Assert.Equal(challenger.ArtifactSha256, selected.ArtifactSha256);
    }

    [Fact]
    public async Task UpdateBestSoFar_TracksAccuracyFirstCandidate_IndependentlyOfRetentionWinner()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient();
        var generationMetricsType = typeof(BasicsExecutionSession).GetNestedType("GenerationMetrics", BindingFlags.NonPublic);
        var updateMethod = typeof(BasicsExecutionSession).GetMethod("UpdateBestSoFar", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(generationMetricsType);
        Assert.NotNull(updateMethod);

        var winningCandidate = CreateBestCandidateSummary(runtimeClient, "or", accuracy: 0.5f, fitness: 0.80f);
        var trackedBestCandidate = CreateBestCandidateSummary(runtimeClient, "and", accuracy: 0.75f, fitness: 0.40f);
        var generationMetrics = Activator.CreateInstance(
            generationMetricsType!,
            0.75f,
            0.75f,
            0.75f,
            0.75f,
            0f,
            0f,
            0.80f,
            0.80f,
            1,
            0,
            0.60d,
            1,
            0f,
            winningCandidate,
            trackedBestCandidate,
            0,
            string.Empty,
            Array.Empty<BasicsExecutionBootstrapCandidateTrace>());
        Assert.NotNull(generationMetrics);

        object?[] arguments = [generationMetrics, 0f, 0f, null];
        updateMethod!.Invoke(null, arguments);

        Assert.Equal(0.75f, Assert.IsType<float>(arguments[1]));
        Assert.Equal(0.80f, Assert.IsType<float>(arguments[2]));
        var selected = Assert.IsType<BasicsExecutionBestCandidateSummary>(arguments[3]);
        Assert.Equal(trackedBestCandidate.ArtifactSha256, selected.ArtifactSha256);
    }

    [Fact]
    public async Task DidGenerationImprove_RecognizesBalancedMultiplicationGain()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient();
        var generationMetricsType = typeof(BasicsExecutionSession).GetNestedType("GenerationMetrics", BindingFlags.NonPublic);
        var method = typeof(BasicsExecutionSession).GetMethod("DidGenerationImprove", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(generationMetricsType);
        Assert.NotNull(method);

        var previousBest = CreateBestCandidateSummary(
            runtimeClient,
            "multiplication",
            accuracy: 0.75f,
            fitness: 0.70f,
            scoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["task_accuracy"] = 0.75f,
                ["balanced_tolerance_accuracy"] = 0.61f,
                ["edge_tolerance_accuracy"] = 0.44f,
                ["interior_tolerance_accuracy"] = 0.70f
            });
        var trackedBestCandidate = CreateBestCandidateSummary(
            runtimeClient,
            "multiplication",
            accuracy: 0.72f,
            fitness: 0.70f,
            scoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["task_accuracy"] = 0.72f,
                ["balanced_tolerance_accuracy"] = 0.80f,
                ["edge_tolerance_accuracy"] = 0.56f,
                ["interior_tolerance_accuracy"] = 0.89f
            });
        var generationMetrics = Activator.CreateInstance(
            generationMetricsType!,
            0.72f,
            0.72f,
            0.80f,
            0.80f,
            0.56f,
            0.89f,
            0.70f,
            0.70f,
            1,
            0,
            0.70d,
            1,
            0f,
            trackedBestCandidate,
            trackedBestCandidate,
            0,
            string.Empty,
            Array.Empty<BasicsExecutionBootstrapCandidateTrace>());
        Assert.NotNull(generationMetrics);

        var improved = Assert.IsType<bool>(method!.Invoke(null, new object?[] { generationMetrics, 0.75f, previousBest }));
        Assert.True(improved);
    }

    [Fact]
    public async Task DidGenerationImprove_IgnoresRawAccuracyGain_WhenBalancedSelectionRegresses()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient();
        var generationMetricsType = typeof(BasicsExecutionSession).GetNestedType("GenerationMetrics", BindingFlags.NonPublic);
        var method = typeof(BasicsExecutionSession).GetMethod("DidGenerationImprove", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(generationMetricsType);
        Assert.NotNull(method);

        var previousBest = CreateBestCandidateSummary(
            runtimeClient,
            "multiplication",
            accuracy: 0.70f,
            fitness: 0.70f,
            scoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["task_accuracy"] = 0.70f,
                ["balanced_tolerance_accuracy"] = 0.80f,
                ["edge_tolerance_accuracy"] = 0.78f,
                ["interior_tolerance_accuracy"] = 0.82f
            });
        var trackedBestCandidate = CreateBestCandidateSummary(
            runtimeClient,
            "multiplication",
            accuracy: 0.85f,
            fitness: 0.75f,
            scoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["task_accuracy"] = 0.85f,
                ["balanced_tolerance_accuracy"] = 0.62f,
                ["edge_tolerance_accuracy"] = 0.35f,
                ["interior_tolerance_accuracy"] = 0.89f
            });
        var generationMetrics = Activator.CreateInstance(
            generationMetricsType!,
            0.85f,
            0.85f,
            0.62f,
            0.62f,
            0.35f,
            0.89f,
            0.75f,
            0.75f,
            1,
            0,
            0.75d,
            1,
            0f,
            trackedBestCandidate,
            trackedBestCandidate,
            0,
            string.Empty,
            Array.Empty<BasicsExecutionBootstrapCandidateTrace>());
        Assert.NotNull(generationMetrics);

        var improved = Assert.IsType<bool>(method!.Invoke(null, new object?[] { generationMetrics, 0.70f, previousBest }));
        Assert.False(improved);
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
    public async Task ExecutionSession_TracksBestCandidateReadyTickMetrics_And_RepeatsCanonicalSamples()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ThrowOnReproduceCallNumber = 2,
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
                        MaxReadyWindowTicks = 8,
                        SampleRepeatCount = 3
                    }
                },
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Failed, final.State);
            Assert.NotNull(final.BestCandidate);
            Assert.Equal(1, final.BestCandidate.Generation);
            Assert.Equal(2f, final.BestCandidate.AverageReadyTickCount);
            Assert.Equal(2f, final.BestCandidate.MinReadyTickCount);
            Assert.Equal(2f, final.BestCandidate.MedianReadyTickCount);
            Assert.Equal(2f, final.BestCandidate.MaxReadyTickCount);
            Assert.True(runtimeClient.ResetBrainRuntimeStateRequests.Count >= 24);
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

            Assert.Contains(final.State, new[] { BasicsExecutionState.Stopped, BasicsExecutionState.Succeeded });
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
    public async Task ExecutionSession_ClampsPlacementWaitConcurrency_ToEligibleWorkerCount()
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

            Assert.Contains(final.State, new[] { BasicsExecutionState.Stopped, BasicsExecutionState.Succeeded });
            Assert.True(runtimeClient.AwaitSpawnPlacementCallCount >= 4);
            Assert.InRange(runtimeClient.MaxObservedConcurrentPlacementWaits, 1, 1);
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
                    RecommendedInitialPopulationCount: 1,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 1,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };
            var final = await session.RunAsync(
                plan,
                new OrTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.NotEqual(BasicsExecutionState.Succeeded, final.State);
            Assert.Equal(4, runtimeClient.SpawnRequestCount);
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
    public async Task ExecutionSession_LimitsPlacementTimeoutRetries_ToOneRetry()
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
                    RecommendedInitialPopulationCount: 1,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 1,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };
            var final = await session.RunAsync(
                plan,
                new OrTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.NotEqual(BasicsExecutionState.Succeeded, final.State);
            Assert.Equal(4, runtimeClient.SpawnRequestCount);
            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Running
                            && snapshot.StatusText.Contains("Evaluating generation 1...", StringComparison.Ordinal)
                            && snapshot.DetailText.Contains("attempt 3/3", StringComparison.Ordinal));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_DefaultPlacementWait_UsesExplicitFiveSecondCallerBudget()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "or",
            RequirePlacementWaitForVisibility = true,
            AwaitPlacementDelay = TimeSpan.FromSeconds(6)
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
                    RecommendedInitialPopulationCount: 1,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 1,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };

            var final = await session.RunAsync(
                plan,
                new OrTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.NotEqual(BasicsExecutionState.Failed, final.State);
            Assert.True(runtimeClient.AwaitSpawnPlacementCallCount > 0);
            Assert.NotEmpty(runtimeClient.AwaitPlacementTimeouts);
            Assert.All(
                runtimeClient.AwaitPlacementTimeouts,
                timeout => Assert.Equal(TimeSpan.FromSeconds(5), timeout));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_DoesNotAbortBatch_For_IsolatedSpawnInternalErrors()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            PersistentSpawnFailureCount = 1,
            PersistentSpawnFailureCode = "spawn_internal_error",
            PersistentSpawnFailureMessage = "artifact-backed shard load failed"
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var plan = CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1,
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f
                    },
                    new AndTaskPlugin()) with
            {
                Capacity = new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 2,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 2,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };

            var final = await session.RunAsync(
                plan,
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.NotEqual(BasicsExecutionState.Failed, final.State);
            Assert.True(runtimeClient.SpawnRequestCount >= 2);
            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.State == BasicsExecutionState.Failed
                            && snapshot.DetailText.Contains("aborted after unrecoverable spawn failure", StringComparison.Ordinal));
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
    public async Task ExecutionSession_PreservesExtendedSpawnInternalErrorDetail_InSummaries()
    {
        const string detailedFailure =
            "Artifact-backed shard load failed for brain 04b04b19-cc33-4bf9-852a-cd63b1a95e9e region 31 shard: " +
            "Snapshot validation failed because the output region overlay length did not match the base definition.";
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            PersistentSpawnFailureCount = 1,
            PersistentSpawnFailureCode = "spawn_internal_error",
            PersistentSpawnFailureMessage = detailedFailure
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var snapshots = new List<BasicsExecutionSnapshot>();
            var plan = CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1,
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f
                    },
                    new AndTaskPlugin()) with
            {
                Capacity = new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 2,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 2,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test")
            };

            var final = await session.RunAsync(
                plan,
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Contains(
                snapshots,
                snapshot => snapshot.EvaluationFailureSummary.Contains("region 31 shard", StringComparison.Ordinal)
                            && snapshot.EvaluationFailureSummary.Contains("output region overlay length", StringComparison.Ordinal));
            Assert.NotEqual(BasicsExecutionState.Failed, final.State);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_KeepsCarryForwardPopulationUnique_WhenReproductionProducesNoChildren()
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

            Assert.True(
                final.State is BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded,
                $"Expected a clean terminal state, observed {final.State}.");
            Assert.Equal(2, final.Generation);
            Assert.Equal(1, final.PopulationCount);
            Assert.Contains(
                snapshots,
                snapshot => snapshot.StatusText.Contains("Generation 2 evaluated.", StringComparison.Ordinal)
                            && snapshot.PopulationCount == 1);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_OnlyPublishesHistories_WhenGenerationCompletes()
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

            Assert.True(
                final.State is BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded,
                $"Expected a clean terminal state, observed {final.State}.");

            var generationOneSummary = Assert.Single(
                snapshots.Where(snapshot =>
                    snapshot.State == BasicsExecutionState.Running
                    && snapshot.StatusText.Contains("Generation 1 evaluated.", StringComparison.Ordinal)));
            var generationTwoProgressSnapshot = snapshots.FirstOrDefault(snapshot =>
                snapshot.State == BasicsExecutionState.Running
                && snapshot.StatusText.Contains("Evaluating generation 2...", StringComparison.Ordinal));
            var generationTwoSummary = Assert.Single(
                snapshots.Where(snapshot =>
                    snapshot.State == BasicsExecutionState.Running
                    && snapshot.StatusText.Contains("Generation 2 evaluated.", StringComparison.Ordinal)));

            Assert.True(generationOneSummary.OffspringBestAccuracy > 0f);
            Assert.True(generationOneSummary.OffspringBestFitness > generationOneSummary.OffspringBestAccuracy);
            Assert.NotNull(generationTwoProgressSnapshot);
            Assert.Empty(generationTwoProgressSnapshot!.OffspringAccuracyHistory);
            Assert.Empty(generationTwoProgressSnapshot.AccuracyHistory);
            Assert.Equal(
                generationOneSummary.OffspringBestAccuracy,
                generationTwoSummary.OffspringAccuracyHistory[^2]);
            Assert.Equal(
                generationOneSummary.BestAccuracy,
                generationTwoSummary.AccuracyHistory[^2]);
            Assert.Equal(
                generationOneSummary.OffspringBestFitness,
                generationTwoSummary.OffspringFitnessHistory[^2]);
            Assert.Equal(
                generationOneSummary.BestFitness,
                generationTwoSummary.BestFitnessHistory[^2]);
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
        Assert.Equal(0f, result.ScoreBreakdown["edge_tolerance_accuracy"]);
        Assert.Equal(0f, result.ScoreBreakdown["interior_tolerance_accuracy"]);
        Assert.Equal(0f, result.ScoreBreakdown["balanced_tolerance_accuracy"]);
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
        var snapshots = new List<BasicsExecutionSnapshot>();

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
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Succeeded, final.State);
            Assert.InRange(runtimeClient.MaxObservedConcurrentSpawnRequests, 1, 2);
            Assert.Contains(
                snapshots,
                static snapshot => snapshot.State == BasicsExecutionState.Running
                                   && snapshot.StatusText.Contains("Evaluating generation 1...", StringComparison.Ordinal)
                                   && snapshot.DetailText.Contains("Batch 1/4", StringComparison.Ordinal));
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
    public async Task ExecutionSession_UsesCompatibilityChecks_ForTemporaryEliteReproductionDuplicates_WithoutGrowingPopulation()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient
        {
            ForceIncompatibleDistinctParents = true,
            ReturnNoChildrenStartingAtReproduceCallNumber = 1,
            DefaultBehavior = "and"
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var seedABytes = runtimeClient.CreateDefinitionBytes("and");
            var seedBBytes = runtimeClient.CreateDefinitionBytes("or");
            var plan = new BasicsEnvironmentPlan(
                SelectedTask: new AndTaskPlugin().Contract,
                SeedTemplate: BasicsSeedTemplateContract.CreateDefault(),
                SizingOverrides: new BasicsSizingOverrides
                {
                    InitialPopulationCount = 2,
                    MinimumPopulationCount = 2,
                    MaximumPopulationCount = 2
                },
                InitialBrainSeeds:
                [
                    new BasicsInitialBrainSeed("seed-a", seedABytes, DuplicateForReproduction: false, BasicsDefinitionAnalyzer.Analyze(seedABytes).Complexity),
                    new BasicsInitialBrainSeed("seed-b", seedBBytes, DuplicateForReproduction: false, BasicsDefinitionAnalyzer.Analyze(seedBBytes).Complexity)
                ],
                Capacity: new BasicsCapacityRecommendation(
                    Source: BasicsCapacitySource.RuntimePlacementInventory,
                    EligibleWorkerCount: 1,
                    RecommendedInitialPopulationCount: 2,
                    RecommendedReproductionRunCount: 1,
                    RecommendedMaxConcurrentBrains: 1,
                    CapacityScore: 1f,
                    EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                    Summary: "test"),
                OutputObservationMode: BasicsOutputObservationMode.EventedOutput,
                OutputSamplingPolicy: new BasicsOutputSamplingPolicy(),
                DiversityPreset: BasicsDiversityPreset.Medium,
                AdaptiveDiversity: new BasicsAdaptiveDiversityOptions(),
                Reproduction: BasicsReproductionPolicy.CreateDefault(),
                Scheduling: BasicsReproductionSchedulingPolicy.Default with
                {
                    ParentSelection = BasicsReproductionSchedulingPolicy.Default.ParentSelection with
                    {
                        EliteFraction = 1d
                    }
                },
                Metrics: BasicsMetricsContract.Default,
                StopCriteria: new BasicsExecutionStopCriteria
                {
                    MaximumGenerations = 2,
                    TargetAccuracy = 1.1f,
                    TargetFitness = 1.1f
                },
                PlannedAtUtc: DateTimeOffset.UtcNow);

            var final = await session.RunAsync(
                plan,
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.Equal(2, final.PopulationCount);
            Assert.NotEmpty(runtimeClient.AssessCompatibilityRequests);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_QueuesAllEliteCrossPairs_BeforeFallbackPairing()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "zero"
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var seedABytes = runtimeClient.CreateDefinitionBytes("and");
            var seedBBytes = runtimeClient.CreateDefinitionBytes("or");
            var seedCBytes = runtimeClient.CreateDefinitionBytes("gt");
            var expectedElitePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ResolvePairKey(seedABytes, seedBBytes),
                ResolvePairKey(seedABytes, seedCBytes),
                ResolvePairKey(seedBBytes, seedCBytes)
            };

            var final = await session.RunAsync(
                CreatePlan(BasicsOutputObservationMode.EventedOutput) with
                {
                    InitialBrainSeeds =
                    [
                        new BasicsInitialBrainSeed("seed-a", seedABytes, DuplicateForReproduction: false, BasicsDefinitionAnalyzer.Analyze(seedABytes).Complexity),
                        new BasicsInitialBrainSeed("seed-b", seedBBytes, DuplicateForReproduction: false, BasicsDefinitionAnalyzer.Analyze(seedBBytes).Complexity),
                        new BasicsInitialBrainSeed("seed-c", seedCBytes, DuplicateForReproduction: false, BasicsDefinitionAnalyzer.Analyze(seedCBytes).Complexity)
                    ],
                    SizingOverrides = new BasicsSizingOverrides
                    {
                        InitialPopulationCount = 3,
                        MinimumPopulationCount = 3,
                        MaximumPopulationCount = 6
                    },
                    Capacity = new BasicsCapacityRecommendation(
                        Source: BasicsCapacitySource.RuntimePlacementInventory,
                        EligibleWorkerCount: 1,
                        RecommendedInitialPopulationCount: 3,
                        RecommendedReproductionRunCount: 1,
                        RecommendedMaxConcurrentBrains: 1,
                        CapacityScore: 1f,
                        EffectiveRamFreeBytes: 8UL * 1024UL * 1024UL * 1024UL,
                        Summary: "test"),
                    Scheduling = BasicsReproductionSchedulingPolicy.Default with
                    {
                        ParentSelection = BasicsReproductionSchedulingPolicy.Default.ParentSelection with
                        {
                            EliteFraction = 1d,
                            ExplorationFraction = 0d
                        },
                        RunAllocation = BasicsReproductionSchedulingPolicy.Default.RunAllocation with
                        {
                            MinRunsPerPair = 1,
                            MaxRunsPerPair = 1
                        }
                    },
                    StopCriteria = new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 2,
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f
                    }
                },
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.True(runtimeClient.ReproduceRequests.Count >= 3);
            var firstThreePairKeys = runtimeClient.ReproduceRequests
                .Take(3)
                .Select(ResolvePairKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expectedElitePairs, firstThreePairKeys);
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
            Assert.Contains(runtimeClient.SynchronizeBrainRuntimeConfigRequests, brainId => brainId != Guid.Empty);

            var operations = runtimeClient.OperationLog.ToArray();
            var homeostasisIndex = Array.IndexOf(operations, "set_homeostasis");
            var syncIndex = Array.IndexOf(operations, "sync_brain_runtime_config");
            var subscribeIndex = Array.IndexOf(operations, "subscribe_outputs_vector");
            var resumeIndex = Array.IndexOf(operations, "resume_brain");
            Assert.True(homeostasisIndex >= 0);
            Assert.True(syncIndex > homeostasisIndex);
            Assert.True(subscribeIndex > syncIndex);
            Assert.True(syncIndex >= 0);
            Assert.True(resumeIndex > syncIndex);
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

            Assert.True(runtimeClient.OperationLog.Count(operation => operation == "pause_brain") >= 4);
            var operations = runtimeClient.OperationLog.ToArray();
            var primeResumeIndex = Array.IndexOf(operations, "resume_brain");
            var firstSamplePauseIndex = Array.FindIndex(operations, primeResumeIndex + 1, operation => operation == "pause_brain");
            var firstSampleResetIndex = Array.FindIndex(operations, firstSamplePauseIndex + 1, operation => operation == "reset_brain_runtime_state");
            var firstSampleSecondResumeIndex = Array.FindIndex(operations, firstSampleResetIndex + 1, operation => operation == "resume_brain");
            Assert.True(primeResumeIndex >= 0);
            Assert.True(firstSamplePauseIndex > primeResumeIndex);
            Assert.True(firstSampleResetIndex > firstSamplePauseIndex);
            Assert.True(firstSampleSecondResumeIndex > firstSampleResetIndex);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_RetriesResetBrainRuntimeState_WhenTickPhaseIsStillInProgress()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "and",
            TransientResetTickPhaseInProgressCount = 2
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.EventedOutput,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    }),
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.True(
                final.State is BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded,
                $"Expected evaluation to complete after reset retries, observed state {final.State}.");
            Assert.True(final.BestAccuracy > 0f);
            Assert.True(runtimeClient.ResetBrainRuntimeStateRequests.Count >= 3);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_AggregatesReadyTickMetrics_And_RepeatsCanonicalSamples()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            ReadySignalDelayTicks = 2
        };
        var session = CreateSession(runtimeClient);
        var snapshots = new List<BasicsExecutionSnapshot>();

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    }) with
                {
                    OutputSamplingPolicy = new BasicsOutputSamplingPolicy
                    {
                        MaxReadyWindowTicks = 4,
                        SampleRepeatCount = 3
                    }
                },
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.Equal(BasicsExecutionState.Stopped, final.State);
            Assert.NotNull(final.BestCandidate);
            Assert.Equal(26, runtimeClient.ResetBrainRuntimeStateRequests.Count);
            Assert.Equal(2f, final.BestCandidate!.AverageReadyTickCount);
            Assert.Equal(2f, final.BestCandidate.MinReadyTickCount);
            Assert.Equal(2f, final.BestCandidate.MedianReadyTickCount);
            Assert.Equal(2f, final.BestCandidate.MaxReadyTickCount);
            Assert.Equal(0f, final.BestCandidate.ReadyTickStdDev);
            Assert.Contains(
                snapshots,
                snapshot => snapshot.BestCandidate is not null
                    && snapshot.BestCandidate.Generation == 1
                    && snapshot.BestCandidate.AverageReadyTickCount == 2f);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_ReportsPlacementWaitTiming_InBatchSummaries()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "or",
            RequirePlacementWaitForVisibility = true,
            AwaitPlacementDelay = TimeSpan.FromMilliseconds(40)
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        MaximumGenerations = 1
                    },
                    new OrTaskPlugin()),
                new OrTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.True(
                final.State is BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded,
                $"Expected a successful terminal state, but observed {final.State}.");
            Assert.NotNull(final.LatestBatchTiming);
            Assert.True(final.LatestBatchTiming!.AveragePlacementWaitSeconds > 0.01d);
            Assert.NotNull(final.LatestGenerationTiming);
            Assert.True(final.LatestGenerationTiming!.AveragePlacementWaitSeconds > 0.01d);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutionSession_ThrottlesIntermediateEvaluationAndBreedingSnapshots()
    {
        var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "zero"
        };
        var session = CreateSession(
            runtimeClient,
            evaluationProgressPublishInterval: TimeSpan.FromDays(1),
            breedingProgressPublishInterval: TimeSpan.FromDays(1));
        var snapshots = new List<BasicsExecutionSnapshot>();

        try
        {
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        TargetAccuracy = 1f,
                        TargetFitness = 0.999f,
                        RequireBothTargets = true,
                        MaximumGenerations = 2
                    },
                    new AndTaskPlugin()),
                new AndTaskPlugin(),
                snapshots.Add,
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.True(
                final.State is BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded,
                $"Expected a clean terminal state, observed {final.State}.");
            Assert.InRange(
                snapshots.Count(snapshot => snapshot.StatusText.StartsWith("Evaluating generation", StringComparison.Ordinal)),
                2,
                12);
            Assert.InRange(
                snapshots.Count(snapshot => snapshot.StatusText.StartsWith("Breeding generation", StringComparison.Ordinal)),
                1,
                1);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public void ExecutionSession_SpeciationParentRef_PrefersArtifactRef_WhenDefinitionIsAvailable()
    {
        var helper = typeof(BasicsExecutionSession).GetMethod(
            "CreateSpeciationParentRef",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(ArtifactRef), typeof(Guid) },
            modifiers: null);
        Assert.NotNull(helper);

        var artifact = new string('a', 64).ToArtifactRef(256, "application/x-nbn", "http://fake-store/parent");
        var liveBrainId = Guid.NewGuid();

        var artifactParent = Assert.IsType<SpeciationParentRef>(helper!.Invoke(null, new object[] { artifact, liveBrainId }));
        Assert.Equal(SpeciationParentRef.ParentOneofCase.ArtifactRef, artifactParent.ParentCase);
        Assert.Equal(artifact.ToSha256Hex(), artifactParent.ArtifactRef.ToSha256Hex());

        var fallbackParent = Assert.IsType<SpeciationParentRef>(helper.Invoke(null, new object[] { new ArtifactRef(), liveBrainId }));
        Assert.Equal(SpeciationParentRef.ParentOneofCase.BrainId, fallbackParent.ParentCase);
        Assert.Equal(liveBrainId, fallbackParent.BrainId.ToGuid());
    }

    [Fact]
    public async Task ExecutionSession_GenerationChildSpeciationAssignments_UseArtifactParentsAndLineageMetadata()
    {
        await using var runtimeClient = new FakeBasicsRuntimeClient
        {
            DefaultBehavior = "zero",
            UseUniqueReproductionChildDefinitions = true
        };
        var session = CreateSession(runtimeClient);

        try
        {
            var seedBytes = runtimeClient.CreateDefinitionBytes("zero");
            var final = await session.RunAsync(
                CreatePlan(
                    BasicsOutputObservationMode.VectorPotential,
                    new BasicsExecutionStopCriteria
                    {
                        TargetAccuracy = 1.1f,
                        TargetFitness = 1.1f,
                        MaximumGenerations = 2
                    }) with
                {
                    InitialBrainSeeds =
                    [
                        new BasicsInitialBrainSeed(
                            "zero-seed",
                            seedBytes,
                            DuplicateForReproduction: false,
                            BasicsDefinitionAnalyzer.Analyze(seedBytes).Complexity)
                    ],
                    SizingOverrides = new BasicsSizingOverrides
                    {
                        InitialPopulationCount = 2,
                        MinimumPopulationCount = 2,
                        MaximumPopulationCount = 3
                    }
                },
                new AndTaskPlugin(),
                _ => { },
                new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

            Assert.True(
                final.State is BasicsExecutionState.Stopped or BasicsExecutionState.Succeeded,
                $"Expected a clean terminal state, observed {final.State}.");
            var generationChildAssignments = runtimeClient.SpeciationAssignRequests
                .Where(static request => string.Equals(request.DecisionReason, "basics_generation_child", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(generationChildAssignments);
            Assert.All(generationChildAssignments, request =>
            {
                Assert.All(
                    request.Parents,
                    parent => Assert.Equal(SpeciationParentRef.ParentOneofCase.ArtifactRef, parent.ParentCase));
                AssertSpeciationMetadataIncludesLineageScores(request.DecisionMetadataJson);
            });
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public void ExecutionSession_SpeciationMetadata_SanitizesNonFiniteSimilarityScores()
    {
        var helper = typeof(BasicsExecutionSession).GetMethod(
            "BuildSpeciationDecisionMetadataJson",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(Repro.SimilarityReport) },
            modifiers: null);
        Assert.NotNull(helper);

        var json = Assert.IsType<string>(helper!.Invoke(null, new object[]
        {
            new Repro.SimilarityReport
            {
                SimilarityScore = float.NaN,
                FunctionScore = float.PositiveInfinity,
                ConnectivityScore = float.NegativeInfinity,
                RegionSpanScore = float.NaN,
                LineageSimilarityScore = float.NaN,
                LineageParentASimilarityScore = float.PositiveInfinity,
                LineageParentBSimilarityScore = float.NegativeInfinity
            }
        }));

        using var metadata = JsonDocument.Parse(json);
        var lineage = metadata.RootElement.GetProperty("lineage");
        Assert.Equal(0f, lineage.GetProperty("lineage_similarity_score").GetSingle());
        Assert.Equal(0f, lineage.GetProperty("parent_a_similarity_score").GetSingle());
        Assert.Equal(0f, lineage.GetProperty("parent_b_similarity_score").GetSingle());
        Assert.Equal(0f, metadata.RootElement.GetProperty("similarity_score").GetSingle());
        Assert.Equal(0f, metadata.RootElement.GetProperty("function_score").GetSingle());
        Assert.Equal(0f, metadata.RootElement.GetProperty("connectivity_score").GetSingle());
        Assert.Equal(0f, metadata.RootElement.GetProperty("region_span_score").GetSingle());
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

    private static void AssertSpeciationMetadataIncludesLineageScores(string metadataJson)
    {
        using var metadata = JsonDocument.Parse(metadataJson);
        var lineage = metadata.RootElement.GetProperty("lineage");
        Assert.True(lineage.GetProperty("lineage_similarity_score").GetSingle() > 0f);
        Assert.True(lineage.GetProperty("parent_a_similarity_score").GetSingle() > 0f);
        Assert.True(lineage.GetProperty("parent_b_similarity_score").GetSingle() > 0f);
    }

    private static BasicsExecutionSession CreateSession(
        FakeBasicsRuntimeClient runtimeClient,
        BasicsTemplatePublishingOptions? publishingOptions = null,
        TimeSpan? minimumSpawnRequestInterval = null,
        TimeSpan? spawnPlacementTimeout = null,
        TimeSpan? evaluationProgressPublishInterval = null,
        TimeSpan? breedingProgressPublishInterval = null)
        => new(
            runtimeClient,
            publishingOptions ?? new BasicsTemplatePublishingOptions { BindHost = "127.0.0.1" },
            minimumSpawnRequestInterval ?? TimeSpan.FromMilliseconds(1),
            spawnPlacementTimeout,
            evaluationProgressPublishInterval,
            breedingProgressPublishInterval);

    private static string ResolvePairKey(Repro.ReproduceByArtifactsRequest request)
        => ResolvePairKey(
            request.ParentADef?.ToSha256Hex() ?? string.Empty,
            request.ParentBDef?.ToSha256Hex() ?? string.Empty);

    private static string ResolvePairKey(byte[] left, byte[] right)
        => ResolvePairKey(
            Convert.ToHexString(SHA256.HashData(left)).ToLowerInvariant(),
            Convert.ToHexString(SHA256.HashData(right)).ToLowerInvariant());

    private static string ResolvePairKey(string leftSha, string rightSha)
        => string.Compare(leftSha, rightSha, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{leftSha}|{rightSha}"
            : $"{rightSha}|{leftSha}";

    private static BasicsExecutionBestCandidateSummary CreateBestCandidateSummary(
        FakeBasicsRuntimeClient runtimeClient,
        string behavior,
        float accuracy,
        float fitness,
        IReadOnlyDictionary<string, float>? scoreBreakdown = null)
        => new(
            runtimeClient.CreateDefinitionArtifact(behavior),
            SnapshotArtifact: null,
            ActiveBrainId: null,
            SpeciesId: "species.default",
            Accuracy: accuracy,
            Fitness: fitness,
            Complexity: new BasicsDefinitionComplexitySummary(1, 1, 3),
            ScoreBreakdown: scoreBreakdown is null
                ? new Dictionary<string, float>(StringComparer.Ordinal)
                {
                    ["task_accuracy"] = accuracy
                }
                : new Dictionary<string, float>(scoreBreakdown, StringComparer.Ordinal),
            Diagnostics: Array.Empty<string>());

    private sealed class FakeBasicsRuntimeClient : IBasicsRuntimeClient
    {
        private readonly Dictionary<Guid, ArtifactRef> _brainDefinitions = new();
        private readonly Dictionary<Guid, ArtifactRef> _brainSnapshots = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputVector>> _outputs = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputVector>> _delayedOutputs = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputEvent>> _outputEvents = new();
        private readonly Dictionary<Guid, Queue<BasicsRuntimeOutputEvent>> _delayedOutputEvents = new();
        private readonly Dictionary<Guid, ulong> _ticks = new();
        private readonly Dictionary<Guid, DateTimeOffset> _outputAvailableAtUtc = new();
        private readonly HashSet<Guid> _resumeDelayApplied = new();
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
        public int PersistentSpawnFailureCount { get; set; }
        public string PersistentSpawnFailureCode { get; init; } = "spawn_internal_error";
        public string PersistentSpawnFailureMessage { get; init; } = "persistent_test_failure";
        public TimeSpan SpawnDelay { get; init; }
        public TimeSpan ResumeOutputActivationDelay { get; init; }
        public TimeSpan ReproduceDelay { get; init; }
        public string DefaultBehavior { get; init; } = "zero";
        public bool UseUniqueReproductionChildDefinitions { get; init; }
        public bool OnlyEmitOutputVectorOnChange { get; init; }
        public int ReadySignalDelayTicks { get; init; } = 1;
        public float PreReadyOutputValue { get; init; }
        public float PreReadyReadyValue { get; init; }
        public bool SuppressOutputVectors { get; init; }
        public bool SuppressReadyOutputEvents { get; init; }
        public bool SuppressPreReadyReadyOutputEvents { get; init; }
        public bool RequirePlacementWaitForVisibility { get; init; }
        public TimeSpan AwaitPlacementDelay { get; init; }
        public string AwaitPlacementFailureCode { get; init; } = "spawn_request_canceled";
        public string AwaitPlacementFailureMessage { get; init; } = "placement visibility timed out";
        public int? OutputVectorWidthOverride { get; init; }
        public bool ReuseSingleChildArtifactOnReproduce { get; init; }
        public bool ForceIncompatibleDistinctParents { get; init; }
        public OutputVectorSource InitialOutputVectorSource { get; init; } = OutputVectorSource.Potential;
        public int TransientResetTickPhaseInProgressCount { get; set; }
        public int MaxObservedConcurrentSpawnRequests => _maxObservedConcurrentSpawnRequests;
        public int MaxObservedConcurrentPlacementWaits => _maxObservedConcurrentPlacementWaits;
        public int AwaitSpawnPlacementCallCount { get; private set; }
        public List<SpeciationAssignRequest> SpeciationAssignRequests { get; } = new();
        public List<Repro.AssessCompatibilityByArtifactsRequest> AssessCompatibilityRequests { get; } = new();
        public List<(Guid BrainId, OutputVectorSource OutputVectorSource)> SetOutputVectorSourceRequests { get; } = new();
        public List<(Guid BrainId, bool Enabled)> SetCostEnergyEnabledRequests { get; } = new();
        public List<(Guid BrainId, bool Enabled)> SetPlasticityEnabledRequests { get; } = new();
        public List<(Guid BrainId, bool Enabled)> SetHomeostasisEnabledRequests { get; } = new();
        public List<Guid> SynchronizeBrainRuntimeConfigRequests { get; } = new();
        public List<(Guid BrainId, bool ResetBuffer, bool ResetAccumulator)> ResetBrainRuntimeStateRequests { get; } = new();
        public List<Repro.ReproduceByArtifactsRequest> ReproduceRequests { get; } = new();
        public List<Repro.ReproduceResult> ReproduceResults { get; } = new();
        public List<TimeSpan> AwaitPlacementTimeouts { get; } = new();
        public List<TimeSpan> VectorWaitTimeouts { get; } = new();
        public List<TimeSpan> EventWaitTimeouts { get; } = new();
        public List<SpawnBrain> SpawnRequests { get; } = new();
        public ConcurrentQueue<string> OperationLog { get; } = new();
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
                SpawnRequests.Add(request.Clone());
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

                if (PersistentSpawnFailureCount > 0)
                {
                    PersistentSpawnFailureCount--;
                    return new SpawnBrainViaIOAck
                    {
                        Ack = new SpawnBrainAck
                        {
                            BrainId = Guid.NewGuid().ToProtoUuid(),
                            AcceptedForPlacement = false,
                            PlacementReady = false,
                            FailureReasonCode = PersistentSpawnFailureCode,
                            FailureMessage = PersistentSpawnFailureMessage
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
            AwaitPlacementTimeouts.Add(timeout);
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
                                FailureReasonCode = AwaitPlacementFailureCode,
                                FailureMessage = AwaitPlacementFailureMessage
                            },
                            FailureReasonCode = AwaitPlacementFailureCode,
                            FailureMessage = AwaitPlacementFailureMessage
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
            OperationLog.Enqueue("subscribe_outputs_vector");
            return Task.CompletedTask;
        }

        public Task SubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default)
        {
            SingleSubscriptionCount++;
            OperationLog.Enqueue("subscribe_outputs");
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
            var outputQueue = IsOutputActive(brainId) ? _outputs[brainId] : _delayedOutputs[brainId];
            var outputEventQueue = IsOutputActive(brainId) ? _outputEvents[brainId] : _delayedOutputEvents[brainId];

            for (var offset = 1; offset <= readyDelayTicks; offset++)
            {
                var tick = ++_ticks[brainId];
                var ready = offset == readyDelayTicks ? 1f : PreReadyReadyValue;
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
                    outputQueue.Enqueue(new BasicsRuntimeOutputVector(brainId, tick, vector));
                }

                _lastVectorOutputByBrain[brainId] = (value, ready);
                if (value >= 0.5f)
                {
                    outputEventQueue.Enqueue(new BasicsRuntimeOutputEvent(brainId, 0, tick, value));
                }

                if (ready >= 0.5f
                    && !SuppressReadyOutputEvents
                    && (offset == readyDelayTicks || !SuppressPreReadyReadyOutputEvents))
                {
                    outputEventQueue.Enqueue(new BasicsRuntimeOutputEvent(brainId, 1, tick, ready));
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

            if (_delayedOutputs.TryGetValue(brainId, out var delayedQueue))
            {
                delayedQueue.Clear();
            }
        }

        public void ResetOutputEventBuffer(Guid brainId)
        {
            if (_outputEvents.TryGetValue(brainId, out var queue))
            {
                queue.Clear();
            }

            if (_delayedOutputEvents.TryGetValue(brainId, out var delayedQueue))
            {
                delayedQueue.Clear();
            }
        }

        public async Task<BasicsRuntimeOutputVector?> WaitForOutputVectorAsync(
            Guid brainId,
            ulong afterTickExclusive,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            VectorWaitTimeouts.Add(timeout);
            var deadline = timeout > TimeSpan.Zero
                ? DateTimeOffset.UtcNow + timeout
                : DateTimeOffset.UtcNow;

            while (true)
            {
                PromoteDelayedOutputs(brainId);
                if (TryDequeueOutput(_outputs, brainId, afterTickExclusive, out var output))
                {
                    return output;
                }

                if (timeout <= TimeSpan.Zero)
                {
                    return null;
                }

                var delay = ResolveDelayedOutputWait(brainId, deadline, vector: true);
                if (delay <= TimeSpan.Zero)
                {
                    return null;
                }

                await Task.Delay(delay, cancellationToken);
            }
        }

        public async Task<BasicsRuntimeOutputEvent?> WaitForOutputEventAsync(
            Guid brainId,
            ulong afterTickExclusive,
            TimeSpan timeout,
            uint? outputIndex = null,
            CancellationToken cancellationToken = default)
        {
            EventWaitTimeouts.Add(timeout);
            var deadline = timeout > TimeSpan.Zero
                ? DateTimeOffset.UtcNow + timeout
                : DateTimeOffset.UtcNow;

            while (true)
            {
                PromoteDelayedOutputs(brainId);
                if (TryDequeueOutputEvent(_outputEvents, brainId, afterTickExclusive, outputIndex, out var output))
                {
                    return output;
                }

                if (timeout <= TimeSpan.Zero)
                {
                    return null;
                }

                var delay = ResolveDelayedOutputWait(brainId, deadline, vector: false);
                if (delay <= TimeSpan.Zero)
                {
                    return null;
                }

                await Task.Delay(delay, cancellationToken);
            }
        }

        public Task<IoCommandAck?> PauseBrainAsync(
            Guid brainId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            OperationLog.Enqueue("pause_brain");
            _outputAvailableAtUtc[brainId] = DateTimeOffset.MaxValue;
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "pause_brain",
                Success = true,
                Message = "queued"
            });
        }

        public Task<IoCommandAck?> ResumeBrainAsync(
            Guid brainId,
            CancellationToken cancellationToken = default)
        {
            OperationLog.Enqueue("resume_brain");
            var activationDelay = _resumeDelayApplied.Add(brainId)
                ? ResumeOutputActivationDelay
                : TimeSpan.Zero;
            _outputAvailableAtUtc[brainId] = DateTimeOffset.UtcNow + activationDelay;
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "resume_brain",
                Success = true,
                Message = "queued"
            });
        }

        public Task<KillBrainViaIOAck?> KillBrainAsync(Guid brainId, string reason, CancellationToken cancellationToken = default)
        {
            _brainDefinitions.Remove(brainId);
            _pendingBrainDefinitions.Remove(brainId);
            _brainSnapshots.Remove(brainId);
            _outputs.Remove(brainId);
            _delayedOutputs.Remove(brainId);
            _outputEvents.Remove(brainId);
            _delayedOutputEvents.Remove(brainId);
            _ticks.Remove(brainId);
            _outputAvailableAtUtc.Remove(brainId);
            _resumeDelayApplied.Remove(brainId);
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
            OperationLog.Enqueue("set_output_vector_source");
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
            OperationLog.Enqueue("set_cost_energy");
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
            OperationLog.Enqueue("set_plasticity");
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
            OperationLog.Enqueue("set_homeostasis");
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "set_homeostasis",
                Success = true,
                Message = "applied"
            });
        }

        public Task<IoCommandAck?> SynchronizeBrainRuntimeConfigAsync(
            Guid brainId,
            CancellationToken cancellationToken = default)
        {
            SynchronizeBrainRuntimeConfigRequests.Add(brainId);
            OperationLog.Enqueue("sync_brain_runtime_config");
            return Task.FromResult<IoCommandAck?>(new IoCommandAck
            {
                BrainId = brainId.ToProtoUuid(),
                Command = "sync_brain_runtime_config",
                Success = true,
                Message = "applied_shards=1"
            });
        }

        public Task<IoCommandAck?> ResetBrainRuntimeStateAsync(
            Guid brainId,
            bool resetBuffer,
            bool resetAccumulator,
            CancellationToken cancellationToken = default)
        {
            ResetBrainRuntimeStateRequests.Add((brainId, resetBuffer, resetAccumulator));
            OperationLog.Enqueue("reset_brain_runtime_state");
            if (TransientResetTickPhaseInProgressCount > 0)
            {
                TransientResetTickPhaseInProgressCount--;
                return Task.FromResult<IoCommandAck?>(new IoCommandAck
                {
                    BrainId = brainId.ToProtoUuid(),
                    Command = "reset_brain_runtime_state",
                    Success = false,
                    Message = "tick_phase_in_progress"
                });
            }

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
                    : UseUniqueReproductionChildDefinitions
                        ? CreateUniqueStoredDefinition(_childIndex++, behavior).Artifact
                        : CreateStoredDefinition(_childIndex++, behavior).Artifact;
                var report = CreateSimilarityReport(0.72f + (runIndex * 0.01f));
                result.Runs.Add(new Repro.ReproduceRunOutcome
                {
                    RunIndex = (uint)runIndex,
                    ChildDef = child.Clone(),
                    Spawned = false,
                    Report = report.Clone()
                });
                if (runIndex == 0)
                {
                    result.ChildDef = child.Clone();
                    result.Report = report.Clone();
                }
            }

            ReproduceResults.Add(result.Clone());
            return result;
        }

        public Task<Repro.ReproduceResult?> AssessCompatibilityByArtifactsAsync(
            Repro.AssessCompatibilityByArtifactsRequest request,
            CancellationToken cancellationToken = default)
        {
            AssessCompatibilityRequests.Add(request.Clone());
            var parentASha = request.ParentADef?.ToSha256Hex() ?? string.Empty;
            var parentBSha = request.ParentBDef?.ToSha256Hex() ?? string.Empty;
            var compatible = !ForceIncompatibleDistinctParents
                             || string.Equals(parentASha, parentBSha, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult<Repro.ReproduceResult?>(new Repro.ReproduceResult
            {
                Report = new Repro.SimilarityReport
                {
                    Compatible = compatible,
                    AbortReason = compatible ? string.Empty : "test_incompatible_distinct_parents"
                },
                RequestedRunCount = 1
            });
        }

        private static Repro.SimilarityReport CreateSimilarityReport(float lineageSimilarity)
            => new()
            {
                Compatible = true,
                RegionSpanScore = 1f,
                FunctionScore = 0.82f,
                ConnectivityScore = 0.76f,
                SimilarityScore = Math.Clamp(lineageSimilarity, 0f, 1f),
                LineageSimilarityScore = Math.Clamp(lineageSimilarity, 0f, 1f),
                LineageParentASimilarityScore = Math.Clamp(lineageSimilarity + 0.02f, 0f, 1f),
                LineageParentBSimilarityScore = Math.Clamp(lineageSimilarity - 0.02f, 0f, 1f)
            };

        public Task<SpeciationAssignResponse?> AssignSpeciationAsync(
            SpeciationAssignRequest request,
            CancellationToken cancellationToken = default)
        {
            SpeciationAssignRequests.Add(request.Clone());
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

        private (byte[] Bytes, ArtifactRef Artifact) CreateUniqueStoredDefinition(int index, string behavior)
        {
            var template = BasicsSeedTemplateContract.CreateDefault() with
            {
                InitialSeedShapeConstraints = new BasicsSeedShapeConstraints
                {
                    MinInternalNeuronCount = Math.Max(1, index + 1),
                    MinAxonCount = Math.Max(3, index + 4)
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
            _delayedOutputs[brainId] = new Queue<BasicsRuntimeOutputVector>();
            _outputEvents[brainId] = new Queue<BasicsRuntimeOutputEvent>();
            _delayedOutputEvents[brainId] = new Queue<BasicsRuntimeOutputEvent>();
            _ticks[brainId] = 0;
            _outputAvailableAtUtc[brainId] = DateTimeOffset.MaxValue;
            _lastVectorOutputByBrain.Remove(brainId);
        }

        private bool IsOutputActive(Guid brainId)
            => _outputAvailableAtUtc.TryGetValue(brainId, out var availableAt)
               && DateTimeOffset.UtcNow >= availableAt;

        private void PromoteDelayedOutputs(Guid brainId)
        {
            if (!IsOutputActive(brainId))
            {
                return;
            }

            if (_delayedOutputs.TryGetValue(brainId, out var delayedVectors)
                && _outputs.TryGetValue(brainId, out var vectors))
            {
                while (delayedVectors.Count > 0)
                {
                    vectors.Enqueue(delayedVectors.Dequeue());
                }
            }

            if (_delayedOutputEvents.TryGetValue(brainId, out var delayedEvents)
                && _outputEvents.TryGetValue(brainId, out var events))
            {
                while (delayedEvents.Count > 0)
                {
                    events.Enqueue(delayedEvents.Dequeue());
                }
            }
        }

        private TimeSpan ResolveDelayedOutputWait(Guid brainId, DateTimeOffset deadline, bool vector)
        {
            var now = DateTimeOffset.UtcNow;
            var remaining = deadline - now;
            if (remaining <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var hasPending = vector
                ? _delayedOutputs.TryGetValue(brainId, out var delayedVectors) && delayedVectors.Count > 0
                : _delayedOutputEvents.TryGetValue(brainId, out var delayedEvents) && delayedEvents.Count > 0;
            if (!hasPending || !_outputAvailableAtUtc.TryGetValue(brainId, out var availableAt))
            {
                return TimeSpan.Zero;
            }

            if (availableAt <= now)
            {
                return TimeSpan.FromMilliseconds(1);
            }

            var wait = availableAt - now;
            return wait > remaining ? remaining : wait;
        }

        private static bool TryDequeueOutput(
            IReadOnlyDictionary<Guid, Queue<BasicsRuntimeOutputVector>> outputs,
            Guid brainId,
            ulong afterTickExclusive,
            out BasicsRuntimeOutputVector? output)
        {
            output = null;
            if (!outputs.TryGetValue(brainId, out var queue))
            {
                return false;
            }

            while (queue.Count > 0)
            {
                var candidate = queue.Dequeue();
                if (candidate.TickId > afterTickExclusive)
                {
                    output = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryDequeueOutputEvent(
            IReadOnlyDictionary<Guid, Queue<BasicsRuntimeOutputEvent>> outputs,
            Guid brainId,
            ulong afterTickExclusive,
            uint? outputIndex,
            out BasicsRuntimeOutputEvent? output)
        {
            output = null;
            if (!outputs.TryGetValue(brainId, out var queue))
            {
                return false;
            }

            while (queue.Count > 0)
            {
                var candidate = queue.Dequeue();
                if (candidate.TickId > afterTickExclusive
                    && (!outputIndex.HasValue || candidate.OutputIndex == outputIndex.Value))
                {
                    output = candidate;
                    return true;
                }
            }

            return false;
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

    }
}
