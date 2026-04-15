using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class MultiplicationTaskPluginTests
{
    private readonly MultiplicationTaskPlugin _plugin = new();

    [Fact]
    public void BuildDeterministicDataset_ReturnsStratifiedSevenBySevenGrid()
    {
        var dataset = _plugin.BuildDeterministicDataset();

        Assert.Equal(49, dataset.Count);
        Assert.Equal((0f, 0f, 0f), (dataset[0].InputA, dataset[0].InputB, dataset[0].ExpectedOutput));
        Assert.Equal((1f, 1f, 1f), (dataset[^1].InputA, dataset[^1].InputB, dataset[^1].ExpectedOutput));
        Assert.Contains(dataset, sample => sample.InputA == 0.5f && Math.Abs(sample.InputB - (2f / 3f)) < 0.000001f);
        Assert.Contains(dataset, sample => Math.Abs(sample.InputA - (2f / 3f)) < 0.000001f && sample.InputB == 0.5f);
        Assert.Contains(dataset, sample => sample.InputA == 0f && sample.InputB == 0.5f);
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

        Assert.InRange(result.Accuracy, 0.28f, 0.29f);
        Assert.InRange(result.ScoreBreakdown["tolerance_accuracy"], 0.28f, 0.29f);
        Assert.InRange(result.ScoreBreakdown["balanced_tolerance_accuracy"], 0.16f, 0.17f);
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

        Assert.InRange(result.ScoreBreakdown["tolerance_accuracy"], 0.52f, 0.54f);
        Assert.Equal(1f, result.ScoreBreakdown["edge_tolerance_accuracy"]);
        Assert.InRange(result.ScoreBreakdown["interior_tolerance_accuracy"], 0.07f, 0.09f);
        Assert.InRange(result.Accuracy, 0.52f, 0.54f);
        Assert.InRange(result.ScoreBreakdown["balanced_tolerance_accuracy"], 0.30f, 0.32f);
        Assert.True(result.Fitness < 0.5f, $"Expected edge-perfect min baseline to be demoted, observed fitness {result.Fitness:0.###}.");
    }

    [Fact]
    public void Evaluate_UsesToleranceAccuracy_ForNearMissOutputs()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var nearMiss = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), Math.Clamp(sample.ExpectedOutput + 0.025f, 0f, 1f))).ToArray());
        var outsideTolerance = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), Math.Clamp(sample.ExpectedOutput + 0.05f, 0f, 1f))).ToArray());

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
    public void Evaluate_AddsBehaviorOccupancyMetrics_WithoutRewardingCollapsedOutput()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var exact = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), sample.ExpectedOutput)).ToArray());
        var collapsed = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((_, index) => new BasicsTaskObservation((ulong)(index + 1), 0f)).ToArray());

        Assert.True(exact.ScoreBreakdown["behavior_output_entropy"] > collapsed.ScoreBreakdown["behavior_output_entropy"]);
        Assert.True(exact.ScoreBreakdown["behavior_response_diversity"] > collapsed.ScoreBreakdown["behavior_response_diversity"]);
        Assert.True(exact.ScoreBreakdown["behavior_selection_signal"] > collapsed.ScoreBreakdown["behavior_selection_signal"]);
        Assert.Equal(0f, collapsed.ScoreBreakdown["behavior_response_diversity"]);
        Assert.Equal(0f, collapsed.ScoreBreakdown["behavior_selection_signal"]);
    }

    [Fact]
    public void Evaluate_GatesNoisyBehaviorOccupancy_WhenReadyConfidenceIsZero()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var noisyNotReady = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset
                .Select((_, index) => new BasicsTaskObservation(
                    (ulong)(index + 1),
                    index % 2 == 0 ? 0f : 1f,
                    ReadyTickCount: 1f,
                    ReadyConfidence: 0f))
                .ToArray());

        Assert.True(noisyNotReady.ScoreBreakdown["behavior_output_entropy"] > 0f);
        Assert.Equal(0f, noisyNotReady.ScoreBreakdown["behavior_occupancy_signal"]);
        Assert.Equal(0f, noisyNotReady.ScoreBreakdown["behavior_auxiliary_fitness"]);
        Assert.Equal(0f, noisyNotReady.ScoreBreakdown["behavior_selection_signal"]);
    }

    [Fact]
    public void Evaluate_StagesBehaviorSelectionPressure_UntilBalancedAccuracyIsUseful()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var edgeOnly = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset
                .Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), Math.Min(sample.InputA, sample.InputB)))
                .ToArray());
        var exact = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), sample.ExpectedOutput)).ToArray());

        Assert.True(edgeOnly.ScoreBreakdown["behavior_auxiliary_fitness"] > 0f);
        Assert.Equal(0f, edgeOnly.ScoreBreakdown["behavior_stage_gate"]);
        Assert.Equal(0f, edgeOnly.ScoreBreakdown["behavior_selection_signal"]);
        Assert.Equal(1f, exact.ScoreBreakdown["behavior_stage_gate"]);
        Assert.True(exact.ScoreBreakdown["behavior_selection_signal"] > 0f);
    }

    [Fact]
    public void Evaluate_PartiallyActivatesBehaviorSelectionPressure_AtPlateauBalancedAccuracy()
    {
        var dataset = _plugin.BuildDeterministicDataset();
        var correctInteriorCount = 0;
        var plateau = _plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) =>
                {
                    var isEdge = sample.InputA is 0f or 1f || sample.InputB is 0f or 1f;
                    var shouldBeCorrect = isEdge || correctInteriorCount++ < 5;
                    return new BasicsTaskObservation(
                        (ulong)(index + 1),
                        shouldBeCorrect ? sample.ExpectedOutput : 0f);
                })
                .ToArray());

        Assert.InRange(plateau.ScoreBreakdown["balanced_tolerance_accuracy"], 0.35f, 0.50f);
        Assert.InRange(plateau.ScoreBreakdown["behavior_stage_gate"], 0f, 1f);
        Assert.True(plateau.ScoreBreakdown["behavior_selection_signal"] > 0f);
    }

    [Fact]
    public void Evaluate_CanDisableBehaviorSelectionPressure()
    {
        var plugin = new MultiplicationTaskPlugin(new BasicsMultiplicationTaskSettings
        {
            BehaviorOccupancyEnabled = false
        });
        var dataset = plugin.BuildDeterministicDataset();
        var exact = plugin.Evaluate(
            CreateValidContext(),
            dataset,
            dataset.Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), sample.ExpectedOutput)).ToArray());

        Assert.True(exact.ScoreBreakdown["behavior_auxiliary_fitness"] > 0f);
        Assert.Equal(0f, exact.ScoreBreakdown["behavior_stage_gate"]);
        Assert.Equal(0f, exact.ScoreBreakdown["behavior_selection_signal"]);
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
