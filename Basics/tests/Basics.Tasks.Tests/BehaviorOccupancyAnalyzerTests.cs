using Nbn.Demos.Behavior;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class BehaviorOccupancyAnalyzerTests
{
    [Fact]
    public void Analyze_DefaultOptions_MatchesGoldenOccupancyVector()
    {
        var samples = new[]
        {
            new BehaviorOccupancySample(0f, 0f, 0f),
            new BehaviorOccupancySample(0.25f, 0.20f, 1f),
            new BehaviorOccupancySample(0.50f, 0.20f, 1f),
            new BehaviorOccupancySample(0.75f, 0.80f, 3f),
            new BehaviorOccupancySample(1.00f, 0.80f, 5f),
            new BehaviorOccupancySample(0.50f, 1.00f, 5f)
        };

        var metrics = BehaviorOccupancyAnalyzer.Analyze(samples, readyConfidence: 0.70f, targetProximityFitness: 0.55f);

        Assert.Equal(0.742098d, metrics.OutputEntropy, precision: 6);
        Assert.Equal(1.000000d, metrics.TransitionEntropy, precision: 6);
        Assert.Equal(0.600000d, metrics.StateOccupancy, precision: 6);
        Assert.Equal(0.742098d, metrics.ReadyTimingEntropy, precision: 6);
        Assert.Equal(0.826235d, metrics.ResponseDiversity, precision: 6);
        Assert.Equal(0.354279d, metrics.OccupancySignal, precision: 6);
        Assert.Equal(0.371611d, metrics.AuxiliaryFitness, precision: 6);
    }

    [Fact]
    public void Analyze_ReportsControllableDiversity_ForMappedOutputs()
    {
        var mapped = Enumerable.Range(0, 8)
            .Select(index =>
            {
                var value = index / 7f;
                return new BehaviorOccupancySample(value, value, index);
            })
            .ToArray();
        var collapsed = mapped
            .Select(sample => sample with { ObservedValue = 0f })
            .ToArray();

        var mappedMetrics = BehaviorOccupancyAnalyzer.Analyze(mapped, readyConfidence: 1f, targetProximityFitness: 1f);
        var collapsedMetrics = BehaviorOccupancyAnalyzer.Analyze(collapsed, readyConfidence: 1f, targetProximityFitness: 1f);

        Assert.True(mappedMetrics.OutputEntropy > collapsedMetrics.OutputEntropy);
        Assert.True(mappedMetrics.ResponseDiversity > collapsedMetrics.ResponseDiversity);
        Assert.True(mappedMetrics.AuxiliaryFitness > collapsedMetrics.AuxiliaryFitness);
        Assert.Equal(0f, collapsedMetrics.ResponseDiversity);
    }

    [Fact]
    public void Analyze_GatesSignals_WhenViabilityIsZero()
    {
        var noisy = Enumerable.Range(0, 8)
            .Select(index => new BehaviorOccupancySample(index / 7f, index % 2 == 0 ? 0f : 1f, index))
            .ToArray();

        var metrics = BehaviorOccupancyAnalyzer.Analyze(noisy, readyConfidence: 0f, targetProximityFitness: 1f);

        Assert.True(metrics.OutputEntropy > 0f);
        Assert.Equal(0f, metrics.OccupancySignal);
        Assert.Equal(0f, metrics.AuxiliaryFitness);
    }

    [Fact]
    public void ResolveStageGate_RampsSmoothlyBetweenStartAndFull()
    {
        Assert.Equal(0f, BehaviorOccupancyAnalyzer.ResolveStageGate(0.34f, 0.35f, 0.50f));
        Assert.InRange(BehaviorOccupancyAnalyzer.ResolveStageGate(0.425f, 0.35f, 0.50f), 0.49f, 0.51f);
        Assert.Equal(1f, BehaviorOccupancyAnalyzer.ResolveStageGate(0.50f, 0.35f, 0.50f));
        Assert.Equal(0f, BehaviorOccupancyAnalyzer.ResolveStageGate(0.50f, 0.50f, 0.35f));
    }
}
