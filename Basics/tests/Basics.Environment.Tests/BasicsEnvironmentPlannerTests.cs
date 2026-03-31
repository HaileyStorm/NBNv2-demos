using Nbn.Demos.Basics.Environment;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Proto.Speciation;
using Nbn.Shared;
using Repro = Nbn.Proto.Repro;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsEnvironmentPlannerTests
{
    [Fact]
    public async Task BuildPlanAsync_UsesIoPlacementInventoryAndCarriesSchedulingPolicy()
    {
        var runtimeClient = new FakeBasicsRuntimeClient(
            placementWorkerInventory: new PlacementWorkerInventoryResult
            {
                Success = true,
                Inventory = new PlacementWorkerInventory
                {
                    Workers =
                    {
                        new PlacementWorkerInventoryEntry
                        {
                            WorkerAddress = "127.0.0.1:12041",
                            WorkerRootActorName = "worker-node",
                            IsAlive = true,
                            CpuScore = 50f,
                            CpuLimitPercent = 100,
                            RamFreeBytes = 16UL * 1024UL * 1024UL * 1024UL,
                            RamTotalBytes = 32UL * 1024UL * 1024UL * 1024UL,
                            ProcessRamUsedBytes = 2UL * 1024UL * 1024UL * 1024UL,
                            RamLimitPercent = 100,
                            StorageFreeBytes = 64UL * 1024UL * 1024UL * 1024UL,
                            StorageTotalBytes = 128UL * 1024UL * 1024UL * 1024UL,
                            StorageLimitPercent = 100
                        }
                    }
                }
            });
        var planner = new BasicsEnvironmentPlanner(runtimeClient);

        var plan = await planner.BuildPlanAsync(new BasicsEnvironmentOptions
        {
            TaskSettings = new BasicsTaskSettings
            {
                Multiplication = new BasicsMultiplicationTaskSettings
                {
                    UniqueInputValueCount = 7,
                    AccuracyTolerance = 0.02f
                }
            }
        });

        Assert.Equal("basics-template-a", plan.SeedTemplate.TemplateId);
        Assert.Equal(BasicsCapacitySource.RuntimePlacementInventory, plan.Capacity.Source);
        Assert.Equal(0.10d, plan.Scheduling.ParentSelection.EliteFraction);
        Assert.Contains(BasicsMetricId.Accuracy, plan.Metrics.RequiredMetrics);
        Assert.NotNull(plan.TaskSettings);
        Assert.Equal(7, plan.TaskSettings!.Multiplication.UniqueInputValueCount);
        Assert.Equal(0.02f, plan.TaskSettings.Multiplication.AccuracyTolerance);
    }

    [Fact]
    public async Task ValidateBrainGeometryAsync_RejectsNonTwoByOneBrains()
    {
        var runtimeClient = new FakeBasicsRuntimeClient(
            brainInfo: new BrainInfo
            {
                BrainId = Guid.NewGuid().ToProtoUuid(),
                InputWidth = 2,
                OutputWidth = 2
            });
        var planner = new BasicsEnvironmentPlanner(runtimeClient);

        var validation = await planner.ValidateBrainGeometryAsync(Guid.NewGuid());

        Assert.False(validation.IsValid);
        Assert.Contains("expected_2x1_got_2x2", validation.FailureReason, StringComparison.Ordinal);
    }

    private sealed class FakeBasicsRuntimeClient : IBasicsRuntimeClient
    {
        private readonly PlacementWorkerInventoryResult? _placementWorkerInventory;
        private readonly BrainInfo? _brainInfo;

        public FakeBasicsRuntimeClient(
            PlacementWorkerInventoryResult? placementWorkerInventory = null,
            BrainInfo? brainInfo = null)
        {
            _placementWorkerInventory = placementWorkerInventory;
            _brainInfo = brainInfo;
        }

        public Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default)
            => Task.FromResult<ConnectAck?>(new ConnectAck { ServerName = clientName, ServerTimeMs = 1 });

        public Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_placementWorkerInventory);

        public Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default)
            => Task.FromResult(_brainInfo);

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

        public Task<BasicsRuntimeOutputVector?> WaitForOutputVectorAsync(
            Guid brainId,
            ulong afterTickExclusive,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult<BasicsRuntimeOutputVector?>(null);

        public Task<BasicsRuntimeOutputEvent?> WaitForOutputEventAsync(
            Guid brainId,
            ulong afterTickExclusive,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult<BasicsRuntimeOutputEvent?>(null);

        public Task<KillBrainViaIOAck?> KillBrainAsync(Guid brainId, string reason, CancellationToken cancellationToken = default)
            => Task.FromResult<KillBrainViaIOAck?>(null);

        public Task<BrainTerminated?> WaitForBrainTerminatedAsync(
            Guid brainId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult<BrainTerminated?>(null);

        public Task<Nbn.Proto.Io.SetOutputVectorSourceAck?> SetOutputVectorSourceAsync(
            Nbn.Proto.Control.OutputVectorSource outputVectorSource,
            Guid? brainId = null,
            CancellationToken cancellationToken = default)
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

        public Task<Repro.ReproduceResult?> ReproduceByArtifactsAsync(
            Repro.ReproduceByArtifactsRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Repro.ReproduceResult?>(null);

        public Task<SpeciationAssignResponse?> AssignSpeciationAsync(
            SpeciationAssignRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<SpeciationAssignResponse?>(null);

        public Task<SpeciationGetConfigResponse?> GetSpeciationConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SpeciationGetConfigResponse?>(null);

        public Task<SpeciationSetConfigResponse?> SetSpeciationConfigAsync(
            SpeciationRuntimeConfig config,
            bool startNewEpoch,
            CancellationToken cancellationToken = default)
            => Task.FromResult<SpeciationSetConfigResponse?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
