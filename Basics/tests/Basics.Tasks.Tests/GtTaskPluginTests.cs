using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class GtTaskPluginTests
{
    private readonly GtTaskPlugin _plugin = new();

    [Fact]
    public void BuildDeterministicDataset_ReturnsFullComparisonGrid()
    {
        var dataset = _plugin.BuildDeterministicDataset();

        Assert.Equal(9, dataset.Count);
        Assert.Equal((0f, 0f, 0f), (dataset[0].InputA, dataset[0].InputB, dataset[0].ExpectedOutput));
        Assert.Equal((0.5f, 0f, 1f), (dataset[3].InputA, dataset[3].InputB, dataset[3].ExpectedOutput));
        Assert.Equal((1f, 1f, 0f), (dataset[^1].InputA, dataset[^1].InputB, dataset[^1].ExpectedOutput));
    }

    [Fact]
    public void Evaluate_ReturnsPerfectScore_ForFullComparisonGrid()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var result = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), sample.ExpectedOutput)).ToArray());

        Assert.Equal(1f, result.Fitness);
        Assert.Equal(1f, result.Accuracy);
        Assert.Equal(1f, result.ScoreBreakdown["comparison_set_coverage"]);
    }

    [Fact]
    public void Evaluate_TreatsEqualityCasesAsFalse()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var observations = dataset
            .Select((sample, index) =>
            {
                var output = sample.InputA == sample.InputB ? 1f : sample.ExpectedOutput;
                return new BasicsTaskObservation((ulong)(index + 1), output);
            })
            .ToArray();

        var result = _plugin.Evaluate(CreateValidContext(), dataset, observations);

        Assert.Equal(6 / 9f, result.Accuracy);
        Assert.True(result.Fitness < 1f);
        Assert.True(result.ScoreBreakdown["negative_mean_output"] > 0.2f);
    }

    private static BasicsTaskEvaluationContext CreateValidContext()
        => new(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true);
}
