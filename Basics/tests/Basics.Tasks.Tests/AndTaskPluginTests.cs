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
    public void BuildDeterministicDataset_UsesConfiguredTruthValues()
    {
        var plugin = new AndTaskPlugin(new BasicsBinaryTruthTableTaskSettings
        {
            LowInputValue = 0.25f,
            HighInputValue = 0.75f
        });

        var dataset = plugin.BuildDeterministicDataset();

        Assert.Collection(
            dataset,
            sample => Assert.Equal((0.25f, 0.25f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0.25f, 0.75f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0.75f, 0.25f, 0f), (sample.InputA, sample.InputB, sample.ExpectedOutput)),
            sample => Assert.Equal((0.75f, 0.75f, 1f), (sample.InputA, sample.InputB, sample.ExpectedOutput)));
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
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth + 1, TickAligned: true),
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

    [Fact]
    public void Evaluate_RewardsNearZeroNegativeOutputs_WithFinerGrainedFitness()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var nearZero = _plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 0.01f),
                new BasicsTaskObservation(2, 0.02f),
                new BasicsTaskObservation(3, 0.03f),
                new BasicsTaskObservation(4, 0.92f)
            });
        var fartherFromZero = _plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 0.10f),
                new BasicsTaskObservation(2, 0.20f),
                new BasicsTaskObservation(3, 0.30f),
                new BasicsTaskObservation(4, 0.92f)
            });

        Assert.Equal(1f, nearZero.Accuracy);
        Assert.Equal(1f, fartherFromZero.Accuracy);
        Assert.True(nearZero.Fitness > fartherFromZero.Fitness);
        Assert.True(nearZero.ScoreBreakdown["negative_mean_output"] < fartherFromZero.ScoreBreakdown["negative_mean_output"]);
        Assert.True(nearZero.ScoreBreakdown["target_proximity_fitness"] > fartherFromZero.ScoreBreakdown["target_proximity_fitness"]);
    }

    [Fact]
    public void Evaluate_DistinguishesBoundaryOutputs_BeyondThresholdAccuracy()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var lowConfidence = _plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 0.01f),
                new BasicsTaskObservation(2, 0.01f),
                new BasicsTaskObservation(3, 0.01f),
                new BasicsTaskObservation(4, 0.51f)
            });
        var highConfidence = _plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true),
            dataset,
            new[]
            {
                new BasicsTaskObservation(1, 0.01f),
                new BasicsTaskObservation(2, 0.01f),
                new BasicsTaskObservation(3, 0.01f),
                new BasicsTaskObservation(4, 0.95f)
            });

        Assert.Equal(1f, lowConfidence.Accuracy);
        Assert.Equal(1f, highConfidence.Accuracy);
        Assert.True(highConfidence.Fitness > lowConfidence.Fitness);
        Assert.True(highConfidence.ScoreBreakdown["positive_mean_gap"] < lowConfidence.ScoreBreakdown["positive_mean_gap"]);
    }
}
