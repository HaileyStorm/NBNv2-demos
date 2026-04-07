using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Shared;

namespace Nbn.Demos.Basics.Environment;

public static class BasicsCapacitySizer
{
    private const ulong Gibibyte = 1024UL * 1024UL * 1024UL;
    private const int FallbackInitialPopulation = 32;
    private const uint FallbackReproductionRunCount = 1;
    private const int FallbackMaxConcurrentBrains = 4;
    private const int MaxInitialPopulation = 4096;
    private const int MaxConcurrentBrains = 256;
    private const uint MaxReproductionRunCount = 32;

    public static BasicsCapacityRecommendation Recommend(
        PlacementWorkerInventoryResult? result,
        BasicsSizingOverrides? overrides = null)
    {
        var recommendation = result is not null
                             && result.Success
                             && result.Inventory is not null
                             && result.Inventory.Workers.Count > 0
            ? Recommend(result.Inventory)
            : BuildFallbackRecommendation(result);

        return ApplyOverrides(recommendation, overrides);
    }

    public static BasicsCapacityRecommendation Recommend(
        PlacementWorkerInventory inventory,
        BasicsSizingOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var eligibleWorkers = 0;
        var effectiveCpuScore = 0f;
        var effectiveGpuScore = 0f;
        var effectiveRamFreeBytes = 0UL;

        foreach (var worker in inventory.Workers)
        {
            eligibleWorkers++;
            effectiveCpuScore += WorkerCapabilityMath.EffectiveCpuScore(worker.CpuScore, worker.CpuLimitPercent);
            effectiveRamFreeBytes += WorkerCapabilityMath.EffectiveRamFreeBytes(
                worker.RamFreeBytes,
                worker.RamTotalBytes,
                worker.ProcessRamUsedBytes,
                worker.RamLimitPercent);

            if (!worker.HasGpu)
            {
                continue;
            }

            effectiveGpuScore += WorkerCapabilityMath.EffectiveGpuScore(worker.GpuScore, worker.GpuComputeLimitPercent);
        }

        var scoreUnits = Math.Max(1d, (effectiveCpuScore / 25d) + (effectiveGpuScore / 40d));
        var memoryUnits = Math.Max(1d, effectiveRamFreeBytes / (double)(4UL * Gibibyte));
        var workerCeiling = Math.Max(1d, eligibleWorkers * 8d);

        var maxConcurrentBrains = ClampInt(
            (int)Math.Floor(Math.Min(scoreUnits * 2d, Math.Min(memoryUnits, workerCeiling))),
            minimum: 1,
            maximum: MaxConcurrentBrains);
        var initialPopulation = ClampInt(
            (int)Math.Round(Math.Max(maxConcurrentBrains * 8d, scoreUnits * 16d), MidpointRounding.AwayFromZero),
            minimum: 16,
            maximum: MaxInitialPopulation);
        var reproductionRunCount = (uint)ClampInt(
            (int)Math.Round(1d + (scoreUnits / 4d), MidpointRounding.AwayFromZero),
            minimum: 1,
            maximum: (int)MaxReproductionRunCount);

        var recommendation = new BasicsCapacityRecommendation(
            Source: BasicsCapacitySource.RuntimePlacementInventory,
            EligibleWorkerCount: eligibleWorkers,
            RecommendedInitialPopulationCount: initialPopulation,
            RecommendedReproductionRunCount: reproductionRunCount,
            RecommendedMaxConcurrentBrains: maxConcurrentBrains,
            CapacityScore: (float)Math.Round(scoreUnits, 3, MidpointRounding.AwayFromZero),
            EffectiveRamFreeBytes: effectiveRamFreeBytes,
            Summary: BuildRuntimePlacementSummary(
                inventory,
                eligibleWorkers,
                effectiveCpuScore,
                effectiveGpuScore,
                effectiveRamFreeBytes));
        return ApplyOverrides(recommendation, overrides);
    }

    private static BasicsCapacityRecommendation BuildFallbackRecommendation(PlacementWorkerInventoryResult? result)
    {
        var reason = result is null
            ? "io capacity response missing"
            : !result.Success
                ? string.Join(
                    "; ",
                    new[]
                    {
                        result.FailureReasonCode,
                        result.FailureMessage
                    }.Where(static value => !string.IsNullOrWhiteSpace(value)))
                : "placement inventory unavailable";

        return new BasicsCapacityRecommendation(
            Source: BasicsCapacitySource.FallbackDefaults,
            EligibleWorkerCount: 0,
            RecommendedInitialPopulationCount: FallbackInitialPopulation,
            RecommendedReproductionRunCount: FallbackReproductionRunCount,
            RecommendedMaxConcurrentBrains: FallbackMaxConcurrentBrains,
            CapacityScore: 1f,
            EffectiveRamFreeBytes: 0UL,
            Summary: string.IsNullOrWhiteSpace(reason)
                ? "fallback defaults"
                : $"fallback defaults ({reason})");
    }

    private static BasicsCapacityRecommendation ApplyOverrides(
        BasicsCapacityRecommendation recommendation,
        BasicsSizingOverrides? overrides)
    {
        if (overrides is null)
        {
            return recommendation;
        }

        return recommendation with
        {
            RecommendedInitialPopulationCount = overrides.InitialPopulationCount ?? recommendation.RecommendedInitialPopulationCount,
            RecommendedReproductionRunCount = overrides.ReproductionRunCount ?? recommendation.RecommendedReproductionRunCount,
            RecommendedMaxConcurrentBrains = overrides.MaxConcurrentBrains ?? recommendation.RecommendedMaxConcurrentBrains
        };
    }

    private static int ClampInt(int value, int minimum, int maximum)
        => Math.Min(maximum, Math.Max(minimum, value));

    private static string BuildRuntimePlacementSummary(
        PlacementWorkerInventory inventory,
        int eligibleWorkers,
        float effectiveCpuScore,
        float effectiveGpuScore,
        ulong effectiveRamFreeBytes)
    {
        var summary = $"placement workers={eligibleWorkers}, cpu_score={effectiveCpuScore:0.###}, gpu_score={effectiveGpuScore:0.###}, ram_gib={effectiveRamFreeBytes / (double)Gibibyte:0.###}";
        if (inventory is null)
        {
            return summary;
        }

        var totalWorkersSeen = (int)Math.Max(0, inventory.TotalWorkersSeen);
        if (totalWorkersSeen > 0)
        {
            summary += $", total_seen={totalWorkersSeen}";
        }

        if (inventory.ExclusionCounts.Count == 0)
        {
            return summary;
        }

        var reasons = inventory.ExclusionCounts
            .OrderByDescending(static entry => entry.Count)
            .ThenBy(static entry => entry.ReasonCode, StringComparer.Ordinal)
            .Select(static entry => $"{entry.ReasonCode}={entry.Count}")
            .ToArray();
        return $"{summary}, excluded[{string.Join(", ", reasons)}]";
    }
}
