using Nbn.Demos.Basics.Environment;
using Nbn.Proto.Control;
using Nbn.Proto.Io;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsCapacitySizerTests
{
    private const ulong Gibibyte = 1024UL * 1024UL * 1024UL;

    [Fact]
    public void Recommend_UsesPlacementInventoryScoresAndRam()
    {
        var result = new PlacementWorkerInventoryResult
        {
            Success = true,
            Inventory = BuildInventory(
                cpuScore: 100f,
                cpuLimitPercent: 50,
                gpuScore: 80f,
                gpuComputeLimitPercent: 50,
                ramFreeBytes: 16 * Gibibyte,
                ramTotalBytes: 32 * Gibibyte,
                processRamUsedBytes: 4 * Gibibyte,
                ramLimitPercent: 50)
        };

        var recommendation = BasicsCapacitySizer.Recommend(result);

        Assert.Equal(BasicsCapacitySource.RuntimePlacementInventory, recommendation.Source);
        Assert.Equal(1, recommendation.EligibleWorkerCount);
        Assert.Equal(48, recommendation.RecommendedInitialPopulationCount);
        Assert.Equal(2u, recommendation.RecommendedReproductionRunCount);
        Assert.Equal(3, recommendation.RecommendedMaxConcurrentBrains);
        Assert.InRange(recommendation.CapacityScore, 2.99f, 3.01f);
    }

    [Fact]
    public void Recommend_AppliesExplicitOverrides()
    {
        var result = new PlacementWorkerInventoryResult
        {
            Success = true,
            Inventory = BuildInventory(
                cpuScore: 100f,
                cpuLimitPercent: 50,
                gpuScore: 0f,
                gpuComputeLimitPercent: 0,
                ramFreeBytes: 16 * Gibibyte,
                ramTotalBytes: 32 * Gibibyte,
                processRamUsedBytes: 4 * Gibibyte,
                ramLimitPercent: 50)
        };
        var overrides = new BasicsSizingOverrides
        {
            InitialPopulationCount = 96,
            ReproductionRunCount = 5,
            MaxConcurrentBrains = 12
        };

        var recommendation = BasicsCapacitySizer.Recommend(result, overrides);

        Assert.Equal(96, recommendation.RecommendedInitialPopulationCount);
        Assert.Equal(5u, recommendation.RecommendedReproductionRunCount);
        Assert.Equal(12, recommendation.RecommendedMaxConcurrentBrains);
    }

    [Fact]
    public void Recommend_IncludesExcludedWorkerReasonsInSummary()
    {
        var inventory = BuildInventory(
            cpuScore: 100f,
            cpuLimitPercent: 50,
            gpuScore: 0f,
            gpuComputeLimitPercent: 0,
            ramFreeBytes: 16 * Gibibyte,
            ramTotalBytes: 32 * Gibibyte,
            processRamUsedBytes: 4 * Gibibyte,
            ramLimitPercent: 50);
        inventory.TotalWorkersSeen = 32;
        inventory.ExclusionCounts.Add(new PlacementWorkerExclusionCount
        {
            ReasonCode = "stale_capabilities",
            Count = 27
        });

        var result = new PlacementWorkerInventoryResult
        {
            Success = true,
            Inventory = inventory
        };

        var recommendation = BasicsCapacitySizer.Recommend(result);

        Assert.Contains("total_seen=32", recommendation.Summary, StringComparison.Ordinal);
        Assert.Contains("stale_capabilities=27", recommendation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Recommend_FallsBackWhenIoCapacityQueryFails()
    {
        var result = new PlacementWorkerInventoryResult
        {
            Success = false,
            FailureReasonCode = "capacity_unavailable",
            FailureMessage = "HiveMind endpoint is not configured."
        };

        var recommendation = BasicsCapacitySizer.Recommend(result);

        Assert.Equal(BasicsCapacitySource.FallbackDefaults, recommendation.Source);
        Assert.Equal(32, recommendation.RecommendedInitialPopulationCount);
        Assert.Equal(1u, recommendation.RecommendedReproductionRunCount);
        Assert.Equal(4, recommendation.RecommendedMaxConcurrentBrains);
    }

    private static PlacementWorkerInventory BuildInventory(
        float cpuScore,
        uint cpuLimitPercent,
        float gpuScore,
        uint gpuComputeLimitPercent,
        ulong ramFreeBytes,
        ulong ramTotalBytes,
        ulong processRamUsedBytes,
        uint ramLimitPercent)
    {
        var inventory = new PlacementWorkerInventory
        {
            SnapshotMs = 123
        };
        inventory.Workers.Add(new PlacementWorkerInventoryEntry
        {
            WorkerAddress = "127.0.0.1:12041",
            WorkerRootActorName = "worker-node",
            IsAlive = true,
            CpuScore = cpuScore,
            CpuLimitPercent = cpuLimitPercent,
            HasGpu = gpuScore > 0f,
            GpuScore = gpuScore,
            GpuComputeLimitPercent = gpuComputeLimitPercent,
            RamFreeBytes = ramFreeBytes,
            RamTotalBytes = ramTotalBytes,
            ProcessRamUsedBytes = processRamUsedBytes,
            RamLimitPercent = ramLimitPercent,
            StorageFreeBytes = 64 * Gibibyte,
            StorageTotalBytes = 128 * Gibibyte,
            StorageLimitPercent = 100
        });
        return inventory;
    }
}
