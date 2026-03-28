using Nbn.Demos.Basics.Environment;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Shared;

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

        var plan = await planner.BuildPlanAsync(new BasicsEnvironmentOptions());

        Assert.Equal("basics-template-a", plan.SeedTemplate.TemplateId);
        Assert.Equal(BasicsCapacitySource.RuntimePlacementInventory, plan.Capacity.Source);
        Assert.Equal(0.10d, plan.Scheduling.ParentSelection.EliteFraction);
        Assert.Contains(BasicsMetricId.Accuracy, plan.Metrics.RequiredMetrics);
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
