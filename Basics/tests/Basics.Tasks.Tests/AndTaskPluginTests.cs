using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class AndTaskPluginTests
{
    private readonly AndTaskPlugin _plugin = new();

    [Fact]
    public void BuildDeterministicDataset_ReturnsFullTruthTable()
    {
        var dataset = _plugin.BuildDeterministicDataset();

        Assert.Collection(
            dataset,
            sample =>
            {
                Assert.Equal(0f, sample.InputA);
                Assert.Equal(0f, sample.InputB);
                Assert.Equal(0f, sample.ExpectedOutput);
            },
            sample =>
            {
                Assert.Equal(0f, sample.InputA);
                Assert.Equal(1f, sample.InputB);
                Assert.Equal(0f, sample.ExpectedOutput);
            },
            sample =>
            {
                Assert.Equal(1f, sample.InputA);
                Assert.Equal(0f, sample.InputB);
                Assert.Equal(0f, sample.ExpectedOutput);
            },
            sample =>
            {
                Assert.Equal(1f, sample.InputA);
                Assert.Equal(1f, sample.InputB);
                Assert.Equal(1f, sample.ExpectedOutput);
            });
    }

    [Fact]
    public void Evaluate_ReturnsPerfectScore_ForPerfectOutputs()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var observations = new[]
        {
            new BasicsTaskObservation(1, 0f),
            new BasicsTaskObservation(2, 0f),
            new BasicsTaskObservation(3, 0f),
            new BasicsTaskObservation(4, 1f)
        };

        var result = _plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true),
            dataset,
            observations);

        Assert.Equal(1f, result.Fitness);
        Assert.Equal(1f, result.Accuracy);
        Assert.Equal(4, result.SamplesEvaluated);
        Assert.Equal(4, result.SamplesCorrect);
        Assert.Equal(1f, result.ScoreBreakdown["truth_table_coverage"]);
    }

    [Fact]
    public void Evaluate_Fails_WhenContextGeometryIsWrong()
    {
        var result = _plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, 2, TickAligned: true),
            _plugin.BuildDeterministicDataset(),
            Array.Empty<BasicsTaskObservation>());

        Assert.Equal(0f, result.Fitness);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("geometry_mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Fails_OnNonFiniteObservation()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var observations = new[]
        {
            new BasicsTaskObservation(1, 0f),
            new BasicsTaskObservation(2, 0f),
            new BasicsTaskObservation(3, float.NaN),
            new BasicsTaskObservation(4, 1f)
        };

        var result = _plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true),
            dataset,
            observations);

        Assert.Equal(0f, result.Fitness);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("non_finite_observation", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_ExposesAndPlugin()
    {
        Assert.True(TaskPluginRegistry.TryGet("and", out var plugin));
        Assert.Equal("AND", plugin.Contract.DisplayName);
    }
}
