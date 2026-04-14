using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class MultiplicationTaskPluginTests
{
    private readonly MultiplicationTaskPlugin _plugin = new();

    [Fact]
    public void BuildDeterministicDataset_ReturnsStratifiedFiveByFiveGrid()
    {
        var dataset = _plugin.BuildDeterministicDataset();

        Assert.Equal(18, dataset.Count);
        Assert.Equal((0f, 0f, 0f), (dataset[0].InputA, dataset[0].InputB, dataset[0].ExpectedOutput));
        Assert.Equal((1f, 1f, 1f), (dataset[^1].InputA, dataset[^1].InputB, dataset[^1].ExpectedOutput));
        Assert.Contains(dataset, sample => sample.InputA == 0.5f && sample.InputB == 0.75f && sample.ExpectedOutput == 0.375f);
        Assert.Contains(dataset, sample => sample.InputA == 0.75f && sample.InputB == 0.5f && sample.ExpectedOutput == 0.375f);
        Assert.DoesNotContain(dataset, sample => sample.InputA == 0f && sample.InputB == 0.5f);
    }

    [Fact]
    public void BuildDeterministicDataset_UsesConfiguredGridSize()
    {
        var plugin = new MultiplicationTaskPlugin(new BasicsMultiplicationTaskSettings
        {
            UniqueInputValueCount = 3
        });

        var dataset = plugin.BuildDeterministicDataset();

        Assert.Equal(5, dataset.Count);
        Assert.Contains(dataset, sample => sample.InputA == 0f && sample.InputB == 0f && sample.ExpectedOutput == 0f);
        Assert.Contains(dataset, sample => sample.InputA == 0f && sample.InputB == 1f && sample.ExpectedOutput == 0f);
        Assert.Contains(dataset, sample => sample.InputA == 0.5f && sample.InputB == 0.5f && sample.ExpectedOutput == 0.25f);
        Assert.Equal((1f, 1f, 1f), (dataset[^1].InputA, dataset[^1].InputB, dataset[^1].ExpectedOutput));
    }

    [Fact]
    public void Evaluate_ReturnsPerfectScore_ForExactProducts()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var result = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), sample.ExpectedOutput)).ToArray());

        Assert.Equal(1f, result.Fitness);
        Assert.Equal(1f, result.Accuracy);
        Assert.Equal(1f, result.ScoreBreakdown["evaluation_set_coverage"]);
        Assert.Equal(1f, result.ScoreBreakdown["tolerance_accuracy"]);
        Assert.Equal(1f, result.ScoreBreakdown["edge_tolerance_accuracy"]);
        Assert.Equal(1f, result.ScoreBreakdown["interior_tolerance_accuracy"]);
        Assert.Equal(1f, result.ScoreBreakdown["balanced_tolerance_accuracy"]);
    }

    [Fact]
    public void Evaluate_DemotesSilentZeroBaseline()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var result = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((_, index) => new BasicsTaskObservation((ulong)(index + 1), 0f)).ToArray());

        Assert.InRange(result.Accuracy, 0.27f, 0.28f);
        Assert.InRange(result.ScoreBreakdown["tolerance_accuracy"], 0.27f, 0.28f);
        Assert.InRange(result.ScoreBreakdown["balanced_tolerance_accuracy"], 0.13f, 0.15f);
        Assert.True(result.Fitness < 0.3f, $"Expected the silent baseline to be penalized, observed fitness {result.Fitness:0.###}.");
        Assert.Equal(1f, result.ScoreBreakdown["unit_product_gap"]);
        Assert.True(result.ScoreBreakdown["midrange_mean_absolute_error"] >= 0.29f);
    }

    [Fact]
    public void Evaluate_DemotesEdgePerfectMinBaseline()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var result = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset
                .Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), Math.Min(sample.InputA, sample.InputB)))
                .ToArray());

        Assert.Equal(0.5f, result.ScoreBreakdown["tolerance_accuracy"]);
        Assert.Equal(1f, result.ScoreBreakdown["edge_tolerance_accuracy"]);
        Assert.Equal(0f, result.ScoreBreakdown["interior_tolerance_accuracy"]);
        Assert.Equal(0.5f, result.Accuracy);
        Assert.InRange(result.ScoreBreakdown["balanced_tolerance_accuracy"], 0.24f, 0.26f);
        Assert.True(result.Fitness < 0.5f, $"Expected edge-perfect min baseline to be demoted, observed fitness {result.Fitness:0.###}.");
    }

    [Fact]
    public void Evaluate_UsesToleranceAccuracy_ForNearMissOutputs()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var nearMiss = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), Math.Clamp(sample.ExpectedOutput + 0.03f, 0f, 1f))).ToArray());
        var outsideTolerance = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), Math.Clamp(sample.ExpectedOutput + 0.08f, 0f, 1f))).ToArray());

        Assert.Equal(1f, nearMiss.Accuracy);
        Assert.True(nearMiss.Fitness < 1f);
        Assert.True(outsideTolerance.Accuracy < nearMiss.Accuracy);
        Assert.True(nearMiss.ScoreBreakdown["midrange_mean_absolute_error"] < outsideTolerance.ScoreBreakdown["midrange_mean_absolute_error"]);
    }

    [Fact]
    public void Evaluate_UsesConfiguredTolerance()
    {
        var strictPlugin = new MultiplicationTaskPlugin(new BasicsMultiplicationTaskSettings
        {
            AccuracyTolerance = 0.02f
        });
        var relaxedPlugin = new MultiplicationTaskPlugin(new BasicsMultiplicationTaskSettings
        {
            AccuracyTolerance = 0.04f
        });
        var dataset = strictPlugin.BuildDeterministicDataset();
        var observations = dataset
            .Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), Math.Clamp(sample.ExpectedOutput + 0.03f, 0f, 1f)))
            .ToArray();

        var strict = strictPlugin.Evaluate(CreateValidContext(), dataset, observations);
        var relaxed = relaxedPlugin.Evaluate(CreateValidContext(), dataset, observations);

        Assert.True(strict.Accuracy < relaxed.Accuracy);
        Assert.True(strict.Fitness < relaxed.Fitness);
    }

    [Fact]
    public void Evaluate_PenalizesLowReadyConfidence()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var ready = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset
                .Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), sample.ExpectedOutput))
                .ToArray());
        var almostNeverReady = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset
                .Select((sample, index) => new BasicsTaskObservation(
                    (ulong)(index + 1),
                    sample.ExpectedOutput,
                    ReadyTickCount: 1f,
                    ReadyConfidence: 0.25f))
                .ToArray());

        Assert.Equal(1f, almostNeverReady.Accuracy);
        Assert.Equal(0.25f, almostNeverReady.ScoreBreakdown["ready_confidence"]);
        Assert.True(almostNeverReady.Fitness < ready.Fitness);
        Assert.InRange(almostNeverReady.Fitness, 0.28f, 0.30f);
    }

    [Fact]
    public void Evaluate_TreatsNonFiniteReadyConfidenceAsNotReady()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var result = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset
                .Select((sample, index) => new BasicsTaskObservation(
                    (ulong)(index + 1),
                    sample.ExpectedOutput,
                    ReadyTickCount: 1f,
                    ReadyConfidence: float.NaN))
                .ToArray());

        Assert.True(float.IsFinite(result.Fitness));
        Assert.Equal(1f, result.Accuracy);
        Assert.Equal(0f, result.ScoreBreakdown["ready_confidence"]);
        Assert.InRange(result.Fitness, 0.04f, 0.06f);
    }

    private static BasicsTaskEvaluationContext CreateValidContext()
        => new(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true);
}
