using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class MultiplicationTaskPluginTests
{
    private readonly MultiplicationTaskPlugin _plugin = new();

    [Fact]
    public void BuildDeterministicDataset_ReturnsFullFiveByFiveGrid()
    {
        var dataset = _plugin.BuildDeterministicDataset();

        Assert.Equal(25, dataset.Count);
        Assert.Equal((0f, 0f, 0f), (dataset[0].InputA, dataset[0].InputB, dataset[0].ExpectedOutput));
        Assert.Equal((0.5f, 0.75f, 0.375f), (dataset[13].InputA, dataset[13].InputB, dataset[13].ExpectedOutput));
        Assert.Equal((1f, 1f, 1f), (dataset[^1].InputA, dataset[^1].InputB, dataset[^1].ExpectedOutput));
    }

    [Fact]
    public void BuildDeterministicDataset_UsesConfiguredGridSize()
    {
        var plugin = new MultiplicationTaskPlugin(new BasicsMultiplicationTaskSettings
        {
            UniqueInputValueCount = 3
        });

        var dataset = plugin.BuildDeterministicDataset();

        Assert.Equal(9, dataset.Count);
        Assert.Equal((0f, 0.5f, 0f), (dataset[1].InputA, dataset[1].InputB, dataset[1].ExpectedOutput));
        Assert.Equal((0.5f, 0.5f, 0.25f), (dataset[4].InputA, dataset[4].InputB, dataset[4].ExpectedOutput));
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

    private static BasicsTaskEvaluationContext CreateValidContext()
        => new(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true);
}
