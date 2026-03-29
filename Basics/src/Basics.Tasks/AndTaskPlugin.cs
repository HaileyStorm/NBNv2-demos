using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

public sealed class AndTaskPlugin : IBasicsTaskPlugin
{
    private static readonly IReadOnlyList<BasicsTaskSample> Dataset =
    [
        new BasicsTaskSample(InputA: 0f, InputB: 0f, ExpectedOutput: 0f, Label: "00"),
        new BasicsTaskSample(InputA: 0f, InputB: 1f, ExpectedOutput: 0f, Label: "01"),
        new BasicsTaskSample(InputA: 1f, InputB: 0f, ExpectedOutput: 0f, Label: "10"),
        new BasicsTaskSample(InputA: 1f, InputB: 1f, ExpectedOutput: 1f, Label: "11")
    ];

    public BasicsTaskContract Contract { get; } = new(
        TaskId: "and",
        DisplayName: "AND",
        InputWidth: BasicsIoGeometry.InputWidth,
        OutputWidth: BasicsIoGeometry.OutputWidth,
        UsesTickAlignedEvaluation: true,
        Description: "Boolean AND over the full deterministic 0/1 truth table.");

    public IReadOnlyList<BasicsTaskSample> BuildDeterministicDataset() => Dataset;

    public BasicsTaskEvaluationResult Evaluate(
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(observations);

        var diagnostics = new List<string>();
        if (context.InputWidth != Contract.InputWidth || context.OutputWidth != Contract.OutputWidth)
        {
            diagnostics.Add($"geometry_mismatch:{context.InputWidth}x{context.OutputWidth}");
            return CreateFailure(diagnostics);
        }

        if (!context.TickAligned)
        {
            diagnostics.Add("tick_alignment_required");
            return CreateFailure(diagnostics);
        }

        if (samples.Count != observations.Count)
        {
            diagnostics.Add($"sample_observation_count_mismatch:{samples.Count}:{observations.Count}");
            return CreateFailure(diagnostics);
        }

        if (samples.Count == 0)
        {
            diagnostics.Add("dataset_empty");
            return CreateFailure(diagnostics);
        }

        var correct = 0;
        var absoluteError = 0f;
        var squaredError = 0f;
        var targetProximityFitnessSum = 0f;
        var negativeOutputSum = 0f;
        var positiveGapSum = 0f;
        var negativeCount = 0;
        var positiveCount = 0;
        ulong? previousTick = null;

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var observation = observations[i];

            if (!float.IsFinite(sample.InputA) || !float.IsFinite(sample.InputB) || !float.IsFinite(sample.ExpectedOutput))
            {
                diagnostics.Add($"non_finite_sample:{i}");
                return CreateFailure(diagnostics);
            }

            if (!float.IsFinite(observation.OutputValue))
            {
                diagnostics.Add($"non_finite_observation:{i}");
                return CreateFailure(diagnostics);
            }

            if (sample.DelayTicks != 0)
            {
                diagnostics.Add($"unexpected_delay_ticks:{i}:{sample.DelayTicks}");
                return CreateFailure(diagnostics);
            }

            if (previousTick.HasValue && observation.TickId < previousTick.Value)
            {
                diagnostics.Add($"tick_order_violation:{previousTick.Value}:{observation.TickId}");
                return CreateFailure(diagnostics);
            }

            previousTick = observation.TickId;

            var expected = sample.ExpectedOutput;
            var observed = observation.OutputValue;
            var delta = Math.Abs(observed - expected);
            absoluteError += delta;
            squaredError += delta * delta;

            var predictedTrue = observed >= 0.5f;
            var expectedTrue = expected >= 0.5f;
            var targetDelta = expectedTrue
                ? Math.Abs(1f - observed)
                : Math.Abs(observed);
            targetProximityFitnessSum += 1f / (1f + (8f * targetDelta));
            if (predictedTrue == expectedTrue)
            {
                correct++;
            }

            if (expectedTrue)
            {
                positiveGapSum += Math.Abs(1f - observed);
                positiveCount++;
            }
            else
            {
                negativeOutputSum += Math.Abs(observed);
                negativeCount++;
            }
        }

        var sampleCount = samples.Count;
        var accuracy = correct / (float)sampleCount;
        var meanAbsoluteError = absoluteError / sampleCount;
        var meanSquaredError = squaredError / sampleCount;
        var targetProximityFitness = targetProximityFitnessSum / sampleCount;
        var negativeMeanOutput = negativeCount == 0 ? 0f : negativeOutputSum / negativeCount;
        var positiveMeanGap = positiveCount == 0 ? 0f : positiveGapSum / positiveCount;
        var fitness = Math.Clamp(
            (0.50f * targetProximityFitness)
            + (0.35f * (1f - meanAbsoluteError))
            + (0.15f * accuracy),
            0f,
            1f);

        return new BasicsTaskEvaluationResult(
            Fitness: fitness,
            Accuracy: accuracy,
            SamplesEvaluated: sampleCount,
            SamplesCorrect: correct,
            ScoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["classification_accuracy"] = accuracy,
                ["mean_absolute_error"] = meanAbsoluteError,
                ["mean_squared_error"] = meanSquaredError,
                ["target_proximity_fitness"] = targetProximityFitness,
                ["negative_mean_output"] = negativeMeanOutput,
                ["positive_mean_gap"] = positiveMeanGap,
                ["truth_table_coverage"] = sampleCount / 4f
            },
            Diagnostics: diagnostics);
    }

    private static BasicsTaskEvaluationResult CreateFailure(IReadOnlyList<string> diagnostics)
        => new(
            Fitness: 0f,
            Accuracy: 0f,
            SamplesEvaluated: 0,
            SamplesCorrect: 0,
            ScoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["classification_accuracy"] = 0f,
                ["mean_absolute_error"] = 1f,
                ["mean_squared_error"] = 1f,
                ["target_proximity_fitness"] = 0f,
                ["negative_mean_output"] = 1f,
                ["positive_mean_gap"] = 1f,
                ["truth_table_coverage"] = 0f
            },
            Diagnostics: diagnostics);
}
