using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class XorTaskPluginTests
{
    private readonly XorTaskPlugin _plugin = new();

    [Fact]
    public void BuildDeterministicDataset_ReturnsFullTruthTable()
    {
        var dataset = _plugin.BuildDeterministicDataset();

        Assert.Collection(
            dataset,
            sample => Assert.Equal((0f, 0f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0f, 1f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((1f, 0f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((1f, 1f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)));
    }

    [Fact]
    public void BuildDeterministicDataset_UsesConfiguredTruthValues()
    {
        var plugin = new XorTaskPlugin(new BasicsBinaryTruthTableTaskSettings
        {
            LowInputValue = 0.1f,
            HighInputValue = 0.9f
        });

        var dataset = plugin.BuildDeterministicDataset();

        Assert.Collection(
            dataset,
            sample => Assert.Equal((0.1f, 0.1f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0.1f, 0.9f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0.9f, 0.1f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0.9f, 0.9f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)));
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
                new BasicsTaskObservation(4, 0f)
            });

        Assert.Equal(1f, result.Fitness);
        Assert.Equal(1f, result.Accuracy);
        Assert.Equal(1f, result.ScoreBreakdown["truth_table_coverage"]);
    }

    [Fact]
    public void Evaluate_PenalizesAlwaysOnOutputs_AgainstExclusivePattern()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var alwaysOn = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 1f),
                new BasicsTaskObservation(2, 1f),
                new BasicsTaskObservation(3, 1f),
                new BasicsTaskObservation(4, 1f)
            });

        Assert.Equal(0.5f, alwaysOn.Accuracy);
        Assert.True(alwaysOn.Fitness < 0.7f);
        Assert.True(alwaysOn.ScoreBreakdown["negative_mean_output"] > 0.9f);
    }

    private static BasicsTaskEvaluationContext CreateValidContext()
        => new(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true);
}
