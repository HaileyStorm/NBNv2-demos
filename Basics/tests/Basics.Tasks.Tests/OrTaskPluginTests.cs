using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class OrTaskPluginTests
{
    private readonly OrTaskPlugin _plugin = new();

    [Fact]
    public void BuildDeterministicDataset_ReturnsFullTruthTable()
    {
        var dataset = _plugin.BuildDeterministicDataset();

        Assert.Collection(
            dataset,
            sample => Assert.Equal((0f, 0f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0f, 1f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((1f, 0f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((1f, 1f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)));
    }

    [Fact]
    public void Evaluate_ReturnsPerfectScore_ForPerfectOutputs()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var result = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 0f),
                new BasicsTaskObservation(2, 1f),
                new BasicsTaskObservation(3, 1f),
                new BasicsTaskObservation(4, 1f)
            });

        Assert.Equal(1f, result.Fitness);
        Assert.Equal(1f, result.Accuracy);
        Assert.Equal(1f, result.ScoreBreakdown["truth_table_coverage"]);
        Assert.Equal(1f, result.ScoreBreakdown["task_accuracy"]);
    }

    [Fact]
    public void Evaluate_RewardsHigherConfidencePositiveOutputs()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var lowConfidence = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 0.01f),
                new BasicsTaskObservation(2, 0.51f),
                new BasicsTaskObservation(3, 0.52f),
                new BasicsTaskObservation(4, 0.53f)
            });
        var highConfidence = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 0.01f),
                new BasicsTaskObservation(2, 0.95f),
                new BasicsTaskObservation(3, 0.96f),
                new BasicsTaskObservation(4, 0.97f)
            });

        Assert.Equal(1f, lowConfidence.Accuracy);
        Assert.Equal(1f, highConfidence.Accuracy);
        Assert.True(highConfidence.Fitness > lowConfidence.Fitness);
        Assert.True(highConfidence.ScoreBreakdown["positive_mean_gap"] < lowConfidence.ScoreBreakdown["positive_mean_gap"]);
    }

    private static BasicsTaskEvaluationContext CreateValidContext()
        => new(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true);
}
