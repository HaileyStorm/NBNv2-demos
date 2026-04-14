using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

internal static class BasicsTaskPluginScoring
{
    private const float TargetProximityScale = 8f;
    private const float ReadyConfidenceFitnessFloor = 0.05f;

    public static BasicsTaskEvaluationResult EvaluateBooleanDataset(
        BasicsTaskContract contract,
        IReadOnlyList<BasicsTaskSample> canonicalDataset,
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations,
        string coverageKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalDataset);

        var failureBreakdown = CreateFailureBreakdown(
            coverageKey,
            ("classification_accuracy", 0f),
            ("negative_mean_output", 1f),
            ("positive_mean_gap", 1f));
        var outcomes = TryBuildOutcomes(contract, canonicalDataset, context, samples, observations, failureBreakdown, out var failure);
        if (outcomes is null)
        {
            return failure!;
        }

        var correct = 0;
        var negativeOutputSum = 0f;
        var positiveGapSum = 0f;
        var negativeCount = 0;
        var positiveCount = 0;

        foreach (var outcome in outcomes)
        {
            var expectedTrue = outcome.Sample.ExpectedOutput >= 0.5f;
            var predictedTrue = outcome.Observation.OutputValue >= 0.5f;
            if (predictedTrue == expectedTrue)
            {
                correct++;
            }

            if (expectedTrue)
            {
                positiveGapSum += Math.Abs(1f - outcome.Observation.OutputValue);
                positiveCount++;
            }
            else
            {
                negativeOutputSum += Math.Abs(outcome.Observation.OutputValue);
                negativeCount++;
            }
        }

        var sampleCount = outcomes.Count;
        var accuracy = correct / (float)sampleCount;
        var breakdown = CreateBaseBreakdown(outcomes, accuracy, coverageKey);
        breakdown["classification_accuracy"] = accuracy;
        breakdown["negative_mean_output"] = negativeCount == 0 ? 0f : negativeOutputSum / negativeCount;
        breakdown["positive_mean_gap"] = positiveCount == 0 ? 0f : positiveGapSum / positiveCount;
        return CreateSuccess(outcomes, correct, accuracy, breakdown);
    }

    public static BasicsTaskEvaluationResult EvaluateBoundedRegressionDataset(
        BasicsTaskContract contract,
        IReadOnlyList<BasicsTaskSample> canonicalDataset,
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations,
        string coverageKey,
        float accuracyTolerance)
    {
        ArgumentNullException.ThrowIfNull(canonicalDataset);
        if (!float.IsFinite(accuracyTolerance) || accuracyTolerance < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(accuracyTolerance));
        }

        var failureBreakdown = CreateFailureBreakdown(
            coverageKey,
            ("tolerance_accuracy", 0f),
            ("zero_product_mean_output", 1f),
            ("unit_product_gap", 1f),
            ("midrange_mean_absolute_error", 1f));
        var outcomes = TryBuildOutcomes(contract, canonicalDataset, context, samples, observations, failureBreakdown, out var failure);
        if (outcomes is null)
        {
            return failure!;
        }

        var correct = 0;
        var zeroOutputSum = 0f;
        var unitGapSum = 0f;
        var midrangeAbsoluteErrorSum = 0f;
        var zeroCount = 0;
        var unitCount = 0;
        var midrangeCount = 0;

        foreach (var outcome in outcomes)
        {
            if (outcome.Delta <= accuracyTolerance)
            {
                correct++;
            }

            if (outcome.Sample.ExpectedOutput == 0f)
            {
                zeroOutputSum += Math.Abs(outcome.Observation.OutputValue);
                zeroCount++;
            }
            else if (outcome.Sample.ExpectedOutput == 1f)
            {
                unitGapSum += Math.Abs(1f - outcome.Observation.OutputValue);
                unitCount++;
            }
            else
            {
                midrangeAbsoluteErrorSum += outcome.Delta;
                midrangeCount++;
            }
        }

        var sampleCount = outcomes.Count;
        var accuracy = correct / (float)sampleCount;
        var breakdown = CreateBaseBreakdown(outcomes, accuracy, coverageKey);
        breakdown["tolerance_accuracy"] = accuracy;
        breakdown["zero_product_mean_output"] = zeroCount == 0 ? 0f : zeroOutputSum / zeroCount;
        breakdown["unit_product_gap"] = unitCount == 0 ? 0f : unitGapSum / unitCount;
        breakdown["midrange_mean_absolute_error"] = midrangeCount == 0 ? 0f : midrangeAbsoluteErrorSum / midrangeCount;
        return CreateBoundedRegressionSuccess(outcomes, correct, accuracy, breakdown);
    }

    public static BasicsTaskEvaluationResult EvaluateMultiplicationDataset(
        BasicsTaskContract contract,
        IReadOnlyList<BasicsTaskSample> canonicalDataset,
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations,
        string coverageKey,
        float accuracyTolerance)
    {
        ArgumentNullException.ThrowIfNull(canonicalDataset);
        if (!float.IsFinite(accuracyTolerance) || accuracyTolerance < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(accuracyTolerance));
        }

        var failureBreakdown = CreateFailureBreakdown(
            coverageKey,
            ("tolerance_accuracy", 0f),
            ("edge_tolerance_accuracy", 0f),
            ("interior_tolerance_accuracy", 0f),
            ("balanced_tolerance_accuracy", 0f),
            ("zero_product_mean_output", 1f),
            ("unit_product_gap", 1f),
            ("midrange_mean_absolute_error", 1f));
        var outcomes = TryBuildOutcomes(contract, canonicalDataset, context, samples, observations, failureBreakdown, out var failure);
        if (outcomes is null)
        {
            return failure!;
        }

        var correct = 0;
        var zeroOutputSum = 0f;
        var unitGapSum = 0f;
        var midrangeAbsoluteErrorSum = 0f;
        var zeroCount = 0;
        var unitCount = 0;
        var midrangeCount = 0;
        var edgeCorrect = 0;
        var edgeCount = 0;
        var interiorCorrect = 0;
        var interiorCount = 0;

        foreach (var outcome in outcomes)
        {
            var sampleCorrect = outcome.Delta <= accuracyTolerance;
            if (sampleCorrect)
            {
                correct++;
            }

            var isEdgeSample = IsMultiplicationEdgeSample(outcome.Sample);
            if (isEdgeSample)
            {
                edgeCount++;
                if (sampleCorrect)
                {
                    edgeCorrect++;
                }
            }
            else
            {
                interiorCount++;
                if (sampleCorrect)
                {
                    interiorCorrect++;
                }
            }

            if (outcome.Sample.ExpectedOutput == 0f)
            {
                zeroOutputSum += Math.Abs(outcome.Observation.OutputValue);
                zeroCount++;
            }
            else if (outcome.Sample.ExpectedOutput == 1f)
            {
                unitGapSum += Math.Abs(1f - outcome.Observation.OutputValue);
                unitCount++;
            }
            else
            {
                midrangeAbsoluteErrorSum += outcome.Delta;
                midrangeCount++;
            }
        }

        var sampleCount = outcomes.Count;
        var rawAccuracy = correct / (float)sampleCount;
        var edgeAccuracy = edgeCount == 0 ? rawAccuracy : edgeCorrect / (float)edgeCount;
        var interiorAccuracy = interiorCount == 0 ? rawAccuracy : interiorCorrect / (float)interiorCount;
        var balancedAccuracy = ResolveMultiplicationBalancedAccuracy(edgeAccuracy, interiorAccuracy);

        var breakdown = CreateBaseBreakdown(outcomes, rawAccuracy, coverageKey);
        breakdown["tolerance_accuracy"] = rawAccuracy;
        breakdown["edge_tolerance_accuracy"] = edgeAccuracy;
        breakdown["interior_tolerance_accuracy"] = interiorAccuracy;
        breakdown["balanced_tolerance_accuracy"] = balancedAccuracy;
        breakdown["zero_product_mean_output"] = zeroCount == 0 ? 0f : zeroOutputSum / zeroCount;
        breakdown["unit_product_gap"] = unitCount == 0 ? 0f : unitGapSum / unitCount;
        breakdown["midrange_mean_absolute_error"] = midrangeCount == 0 ? 0f : midrangeAbsoluteErrorSum / midrangeCount;
        return CreateMultiplicationSuccess(outcomes, correct, rawAccuracy, breakdown);
    }

    private static IReadOnlyList<DeterministicScalarOutcome>? TryBuildOutcomes(
        BasicsTaskContract contract,
        IReadOnlyList<BasicsTaskSample> canonicalDataset,
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations,
        IReadOnlyDictionary<string, float> failureBreakdown,
        out BasicsTaskEvaluationResult? failure)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(failureBreakdown);

        var diagnostics = new List<string>();
        if (context.InputWidth != contract.InputWidth || context.OutputWidth != contract.OutputWidth)
        {
            diagnostics.Add($"geometry_mismatch:{context.InputWidth}x{context.OutputWidth}");
            failure = CreateFailure(diagnostics, failureBreakdown);
            return null;
        }

        if (!context.TickAligned)
        {
            diagnostics.Add("tick_alignment_required");
            failure = CreateFailure(diagnostics, failureBreakdown);
            return null;
        }

        if (samples.Count != observations.Count)
        {
            diagnostics.Add($"sample_observation_count_mismatch:{samples.Count}:{observations.Count}");
            failure = CreateFailure(diagnostics, failureBreakdown);
            return null;
        }

        if (samples.Count == 0)
        {
            diagnostics.Add("dataset_empty");
            failure = CreateFailure(diagnostics, failureBreakdown);
            return null;
        }

        if (samples.Count != canonicalDataset.Count)
        {
            diagnostics.Add($"dataset_cardinality_mismatch:{samples.Count}:{canonicalDataset.Count}");
            failure = CreateFailure(diagnostics, failureBreakdown);
            return null;
        }

        var outcomes = new DeterministicScalarOutcome[samples.Count];
        ulong? previousTick = null;

        for (var i = 0; i < samples.Count; i++)
        {
            var canonicalSample = canonicalDataset[i];
            var sample = samples[i];
            var observation = observations[i];

            if (!MatchesCanonicalSample(sample, canonicalSample))
            {
                diagnostics.Add($"dataset_sample_mismatch:{i}");
                failure = CreateFailure(diagnostics, failureBreakdown);
                return null;
            }

            if (!float.IsFinite(sample.InputA) || !float.IsFinite(sample.InputB) || !float.IsFinite(sample.ExpectedOutput))
            {
                diagnostics.Add($"non_finite_sample:{i}");
                failure = CreateFailure(diagnostics, failureBreakdown);
                return null;
            }

            if (!float.IsFinite(observation.OutputValue))
            {
                diagnostics.Add($"non_finite_observation:{i}");
                failure = CreateFailure(diagnostics, failureBreakdown);
                return null;
            }

            if (sample.DelayTicks != 0)
            {
                diagnostics.Add($"unexpected_delay_ticks:{i}:{sample.DelayTicks}");
                failure = CreateFailure(diagnostics, failureBreakdown);
                return null;
            }

            if (previousTick.HasValue && observation.TickId < previousTick.Value)
            {
                diagnostics.Add($"tick_order_violation:{previousTick.Value}:{observation.TickId}");
                failure = CreateFailure(diagnostics, failureBreakdown);
                return null;
            }

            previousTick = observation.TickId;
            outcomes[i] = new DeterministicScalarOutcome(sample, observation, Math.Abs(observation.OutputValue - sample.ExpectedOutput));
        }

        failure = null;
        return outcomes;
    }

    private static bool MatchesCanonicalSample(BasicsTaskSample sample, BasicsTaskSample canonical)
        => sample.InputA == canonical.InputA
           && sample.InputB == canonical.InputB
           && sample.ExpectedOutput == canonical.ExpectedOutput
           && sample.DelayTicks == canonical.DelayTicks;

    private static Dictionary<string, float> CreateBaseBreakdown(
        IReadOnlyList<DeterministicScalarOutcome> outcomes,
        float accuracy,
        string coverageKey)
    {
        var sampleCount = outcomes.Count;
        var absoluteError = 0f;
        var squaredError = 0f;
        var targetProximityFitnessSum = 0f;

        foreach (var outcome in outcomes)
        {
            absoluteError += outcome.Delta;
            squaredError += outcome.Delta * outcome.Delta;
            targetProximityFitnessSum += 1f / (1f + (TargetProximityScale * outcome.Delta));
        }

        var meanAbsoluteError = absoluteError / sampleCount;
        var meanSquaredError = squaredError / sampleCount;
        var targetProximityFitness = targetProximityFitnessSum / sampleCount;

        var breakdown = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["task_accuracy"] = accuracy,
            ["mean_absolute_error"] = meanAbsoluteError,
            ["mean_squared_error"] = meanSquaredError,
            ["target_proximity_fitness"] = targetProximityFitness,
            ["ready_confidence"] = ComputeReadyConfidence(outcomes),
            ["dataset_coverage"] = 1f
        };

        if (!string.Equals(coverageKey, "dataset_coverage", StringComparison.Ordinal))
        {
            breakdown[coverageKey] = 1f;
        }

        return breakdown;
    }

    private static BasicsTaskEvaluationResult CreateSuccess(
        IReadOnlyList<DeterministicScalarOutcome> outcomes,
        int correct,
        float accuracy,
        IReadOnlyDictionary<string, float> breakdown)
    {
        var fitness = ApplyReadyConfidenceFitness(ComputeSharedFitness(accuracy, breakdown), breakdown);

        return new BasicsTaskEvaluationResult(
            Fitness: fitness,
            Accuracy: accuracy,
            SamplesEvaluated: outcomes.Count,
            SamplesCorrect: correct,
            ScoreBreakdown: breakdown,
            Diagnostics: Array.Empty<string>());
    }

    private static BasicsTaskEvaluationResult CreateBoundedRegressionSuccess(
        IReadOnlyList<DeterministicScalarOutcome> outcomes,
        int correct,
        float accuracy,
        IReadOnlyDictionary<string, float> breakdown)
    {
        var sharedFitness = ComputeSharedFitness(accuracy, breakdown);
        var unitProductScore = 1f - Math.Clamp(breakdown["unit_product_gap"], 0f, 1f);
        var midrangeScore = 1f - Math.Clamp(breakdown["midrange_mean_absolute_error"], 0f, 1f);
        var structuredRegressionScore = (0.55f * unitProductScore) + (0.45f * midrangeScore);
        var fitness = ApplyReadyConfidenceFitness(Math.Clamp(
            sharedFitness * (0.35f + (0.65f * structuredRegressionScore)),
            0f,
            1f), breakdown);

        return new BasicsTaskEvaluationResult(
            Fitness: fitness,
            Accuracy: accuracy,
            SamplesEvaluated: outcomes.Count,
            SamplesCorrect: correct,
            ScoreBreakdown: breakdown,
            Diagnostics: Array.Empty<string>());
    }

    private static BasicsTaskEvaluationResult CreateMultiplicationSuccess(
        IReadOnlyList<DeterministicScalarOutcome> outcomes,
        int correct,
        float accuracy,
        IReadOnlyDictionary<string, float> breakdown)
    {
        var balancedAccuracy = breakdown.TryGetValue("balanced_tolerance_accuracy", out var balanced)
            ? Math.Clamp(balanced, 0f, 1f)
            : accuracy;
        var sharedFitness = Math.Clamp(
            (0.50f * breakdown["target_proximity_fitness"])
            + (0.25f * (1f - breakdown["mean_absolute_error"]))
            + (0.25f * balancedAccuracy),
            0f,
            1f);
        var zeroProductScore = 1f - Math.Clamp(breakdown["zero_product_mean_output"], 0f, 1f);
        var unitProductScore = 1f - Math.Clamp(breakdown["unit_product_gap"], 0f, 1f);
        var midrangeScore = 1f - Math.Clamp(breakdown["midrange_mean_absolute_error"], 0f, 1f);
        var interiorAccuracy = breakdown.TryGetValue("interior_tolerance_accuracy", out var interior)
            ? Math.Clamp(interior, 0f, 1f)
            : accuracy;
        var structuredRegressionScore = Math.Clamp(
            (0.20f * unitProductScore)
            + (0.15f * zeroProductScore)
            + (0.45f * interiorAccuracy)
            + (0.20f * midrangeScore),
            0f,
            1f);
        var fitness = ApplyReadyConfidenceFitness(Math.Clamp(
            sharedFitness * (0.25f + (0.75f * structuredRegressionScore)),
            0f,
            1f), breakdown);

        return new BasicsTaskEvaluationResult(
            Fitness: fitness,
            Accuracy: accuracy,
            SamplesEvaluated: outcomes.Count,
            SamplesCorrect: correct,
            ScoreBreakdown: breakdown,
            Diagnostics: Array.Empty<string>());
    }

    private static bool IsMultiplicationEdgeSample(BasicsTaskSample sample)
        => sample.InputA is 0f or 1f || sample.InputB is 0f or 1f;

    private static float ResolveMultiplicationBalancedAccuracy(float edgeAccuracy, float interiorAccuracy)
        => Math.Clamp((0.25f * edgeAccuracy) + (0.75f * interiorAccuracy), 0f, 1f);

    private static float ComputeSharedFitness(
        float accuracy,
        IReadOnlyDictionary<string, float> breakdown)
        => Math.Clamp(
            (0.50f * breakdown["target_proximity_fitness"])
            + (0.35f * (1f - breakdown["mean_absolute_error"]))
            + (0.15f * accuracy),
            0f,
            1f);

    private static BasicsTaskEvaluationResult CreateFailure(
        IReadOnlyList<string> diagnostics,
        IReadOnlyDictionary<string, float> breakdown)
        => new(
            Fitness: 0f,
            Accuracy: 0f,
            SamplesEvaluated: 0,
            SamplesCorrect: 0,
            ScoreBreakdown: breakdown,
            Diagnostics: diagnostics);

    private static IReadOnlyDictionary<string, float> CreateFailureBreakdown(
        string coverageKey,
        params (string Key, float Value)[] additionalEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coverageKey);

        var breakdown = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["task_accuracy"] = 0f,
            ["mean_absolute_error"] = 1f,
            ["mean_squared_error"] = 1f,
            ["target_proximity_fitness"] = 0f,
            ["ready_confidence"] = 0f,
            ["dataset_coverage"] = 0f
        };

        if (!string.Equals(coverageKey, "dataset_coverage", StringComparison.Ordinal))
        {
            breakdown[coverageKey] = 0f;
        }

        foreach (var (key, value) in additionalEntries)
        {
            breakdown[key] = value;
        }

        return breakdown;
    }

    private readonly record struct DeterministicScalarOutcome(
        BasicsTaskSample Sample,
        BasicsTaskObservation Observation,
        float Delta);

    private static float ComputeReadyConfidence(IReadOnlyList<DeterministicScalarOutcome> outcomes)
        => outcomes.Count == 0
            ? 0f
            : Math.Clamp(
                outcomes.Average(static outcome => ClampUnitFinite(outcome.Observation.ReadyConfidence)),
                0f,
                1f);

    private static float ApplyReadyConfidenceFitness(
        float fitness,
        IReadOnlyDictionary<string, float> breakdown)
    {
        var readyConfidence = breakdown.TryGetValue("ready_confidence", out var value)
            ? ClampUnitFinite(value)
            : 1f;
        var multiplier = ReadyConfidenceFitnessFloor + ((1f - ReadyConfidenceFitnessFloor) * readyConfidence);
        return Math.Clamp(fitness * multiplier, 0f, 1f);
    }

    private static float ClampUnitFinite(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
