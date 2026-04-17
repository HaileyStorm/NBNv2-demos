using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Behavior;

namespace Nbn.Demos.Basics.Tasks;

internal static class BasicsTaskPluginScoring
{
    private const float TargetProximityScale = 8f;
    private const float ReadyConfidenceFitnessFloor = 0.05f;
    private const float BehaviorFitnessBonusWeight = 0.04f;
    private const float MultiplicationBalancedInteriorWeight = 0.65f;
    private const float MultiplicationBalancedEdgeWeight = 0.25f;
    private const float MultiplicationBalancedWeakSideWeight = 0.10f;

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
        float accuracyTolerance,
        bool behaviorOccupancyEnabled,
        float behaviorStageGateStart,
        float behaviorStageGateFull)
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
            ("weak_side_tolerance_accuracy", 0f),
            ("multiplication_surface_fitness", 0f),
            ("multiplication_monotonicity", 0f),
            ("multiplication_product_correlation", 0f),
            ("interior_proximity_fitness", 0f),
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
        var surface = ComputeMultiplicationSurfaceMetrics(outcomes, edgeAccuracy, interiorAccuracy);

        var breakdown = CreateBaseBreakdown(outcomes, rawAccuracy, coverageKey);
        breakdown["tolerance_accuracy"] = rawAccuracy;
        breakdown["edge_tolerance_accuracy"] = edgeAccuracy;
        breakdown["interior_tolerance_accuracy"] = interiorAccuracy;
        breakdown["balanced_tolerance_accuracy"] = balancedAccuracy;
        breakdown["weak_side_tolerance_accuracy"] = surface.WeakSideAccuracy;
        breakdown["multiplication_surface_fitness"] = surface.SurfaceFitness;
        breakdown["multiplication_monotonicity"] = surface.Monotonicity;
        breakdown["multiplication_product_correlation"] = surface.ProductCorrelation;
        breakdown["interior_proximity_fitness"] = surface.InteriorProximityFitness;
        breakdown["zero_product_mean_output"] = zeroCount == 0 ? 0f : zeroOutputSum / zeroCount;
        breakdown["unit_product_gap"] = unitCount == 0 ? 0f : unitGapSum / unitCount;
        breakdown["midrange_mean_absolute_error"] = midrangeCount == 0 ? 0f : midrangeAbsoluteErrorSum / midrangeCount;
        ApplyBehaviorStageGate(
            breakdown,
            balancedAccuracy,
            behaviorOccupancyEnabled,
            behaviorStageGateStart,
            behaviorStageGateFull);
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
        AddBehaviorOccupancyMetrics(breakdown, outcomes);

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
        var surfaceFitness = breakdown.TryGetValue("multiplication_surface_fitness", out var surface)
            ? Math.Clamp(surface, 0f, 1f)
            : 0f;
        var weakSideAccuracy = breakdown.TryGetValue("weak_side_tolerance_accuracy", out var weakSide)
            ? Math.Clamp(weakSide, 0f, 1f)
            : balancedAccuracy;
        var sharedFitness = Math.Clamp(
            (0.42f * breakdown["target_proximity_fitness"])
            + (0.20f * (1f - breakdown["mean_absolute_error"]))
            + (0.20f * balancedAccuracy)
            + (0.18f * surfaceFitness),
            0f,
            1f);
        var zeroProductScore = 1f - Math.Clamp(breakdown["zero_product_mean_output"], 0f, 1f);
        var unitProductScore = 1f - Math.Clamp(breakdown["unit_product_gap"], 0f, 1f);
        var midrangeScore = 1f - Math.Clamp(breakdown["midrange_mean_absolute_error"], 0f, 1f);
        var interiorAccuracy = breakdown.TryGetValue("interior_tolerance_accuracy", out var interior)
            ? Math.Clamp(interior, 0f, 1f)
            : accuracy;
        var structuredRegressionScore = Math.Clamp(
            (0.15f * unitProductScore)
            + (0.10f * zeroProductScore)
            + (0.25f * interiorAccuracy)
            + (0.20f * midrangeScore)
            + (0.20f * weakSideAccuracy)
            + (0.10f * surfaceFitness),
            0f,
            1f);
        var baseFitness = Math.Clamp(
            sharedFitness * (0.22f + (0.78f * structuredRegressionScore)),
            0f,
            1f);
        var fitness = ApplyReadyConfidenceFitness(ApplyBehaviorAuxiliaryBonus(baseFitness, breakdown), breakdown);

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
    {
        edgeAccuracy = Math.Clamp(edgeAccuracy, 0f, 1f);
        interiorAccuracy = Math.Clamp(interiorAccuracy, 0f, 1f);
        var weakSideAccuracy = ComputeWeakSideAccuracy(edgeAccuracy, interiorAccuracy);
        return Math.Clamp(
            (MultiplicationBalancedInteriorWeight * interiorAccuracy)
            + (MultiplicationBalancedEdgeWeight * edgeAccuracy)
            + (MultiplicationBalancedWeakSideWeight * weakSideAccuracy),
            0f,
            1f);
    }

    private static MultiplicationSurfaceMetrics ComputeMultiplicationSurfaceMetrics(
        IReadOnlyList<DeterministicScalarOutcome> outcomes,
        float edgeAccuracy,
        float interiorAccuracy)
    {
        var weakSideAccuracy = ComputeWeakSideAccuracy(edgeAccuracy, interiorAccuracy);
        var monotonicity = ComputeMultiplicationMonotonicity(outcomes);
        var productCorrelation = ComputeProductCorrelation(outcomes);
        var interiorOutcomes = outcomes
            .Where(static outcome => !IsMultiplicationEdgeSample(outcome.Sample))
            .ToArray();
        var interiorProximity = interiorOutcomes.Length == 0
            ? 1f
            : interiorOutcomes.Average(static outcome => 1f / (1f + (TargetProximityScale * outcome.Delta)));
        var surfaceFitness = Math.Clamp(
            (0.35f * productCorrelation)
            + (0.25f * monotonicity)
            + (0.25f * interiorProximity)
            + (0.15f * weakSideAccuracy),
            0f,
            1f);

        return new MultiplicationSurfaceMetrics(
            SurfaceFitness: surfaceFitness,
            Monotonicity: monotonicity,
            ProductCorrelation: productCorrelation,
            InteriorProximityFitness: Math.Clamp(interiorProximity, 0f, 1f),
            WeakSideAccuracy: weakSideAccuracy);
    }

    private static float ComputeWeakSideAccuracy(float edgeAccuracy, float interiorAccuracy)
    {
        edgeAccuracy = Math.Clamp(edgeAccuracy, 0f, 1f);
        interiorAccuracy = Math.Clamp(interiorAccuracy, 0f, 1f);
        var minimum = Math.Min(edgeAccuracy, interiorAccuracy);
        var harmonic = edgeAccuracy <= 0f || interiorAccuracy <= 0f
            ? 0f
            : (2f * edgeAccuracy * interiorAccuracy) / (edgeAccuracy + interiorAccuracy);
        return Math.Clamp((0.65f * minimum) + (0.35f * harmonic), 0f, 1f);
    }

    private static float ComputeMultiplicationMonotonicity(IReadOnlyList<DeterministicScalarOutcome> outcomes)
    {
        var comparablePairs = 0;
        var monotonicPairs = 0;
        for (var i = 0; i < outcomes.Count; i++)
        {
            for (var j = i + 1; j < outcomes.Count; j++)
            {
                var left = outcomes[i];
                var right = outcomes[j];
                if (left.Sample.InputB == right.Sample.InputB && left.Sample.InputA != right.Sample.InputA)
                {
                    comparablePairs++;
                    if (IsNonDecreasingByInput(left, right, compareInputA: true))
                    {
                        monotonicPairs++;
                    }
                }

                if (left.Sample.InputA == right.Sample.InputA && left.Sample.InputB != right.Sample.InputB)
                {
                    comparablePairs++;
                    if (IsNonDecreasingByInput(left, right, compareInputA: false))
                    {
                        monotonicPairs++;
                    }
                }
            }
        }

        return comparablePairs == 0 ? 1f : monotonicPairs / (float)comparablePairs;
    }

    private static bool IsNonDecreasingByInput(
        DeterministicScalarOutcome left,
        DeterministicScalarOutcome right,
        bool compareInputA)
    {
        var leftInput = compareInputA ? left.Sample.InputA : left.Sample.InputB;
        var rightInput = compareInputA ? right.Sample.InputA : right.Sample.InputB;
        var low = leftInput <= rightInput ? left : right;
        var high = leftInput <= rightInput ? right : left;
        return high.Observation.OutputValue + 0.0001f >= low.Observation.OutputValue;
    }

    private static float ComputeProductCorrelation(IReadOnlyList<DeterministicScalarOutcome> outcomes)
    {
        if (outcomes.Count < 2)
        {
            return 1f;
        }

        var expectedMean = outcomes.Average(static outcome => outcome.Sample.ExpectedOutput);
        var outputMean = outcomes.Average(static outcome => Math.Clamp(outcome.Observation.OutputValue, 0f, 1f));
        var numerator = 0d;
        var expectedVariance = 0d;
        var outputVariance = 0d;
        foreach (var outcome in outcomes)
        {
            var expectedDelta = outcome.Sample.ExpectedOutput - expectedMean;
            var outputDelta = Math.Clamp(outcome.Observation.OutputValue, 0f, 1f) - outputMean;
            numerator += expectedDelta * outputDelta;
            expectedVariance += expectedDelta * expectedDelta;
            outputVariance += outputDelta * outputDelta;
        }

        var denominator = Math.Sqrt(expectedVariance * outputVariance);
        if (denominator <= 0.0000001d)
        {
            return 0f;
        }

        return Math.Clamp((float)(numerator / denominator), 0f, 1f);
    }

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
            [BehaviorMetricKeys.OccupancyEnabled] = 0f,
            [BehaviorMetricKeys.OutputEntropy] = 0f,
            [BehaviorMetricKeys.TransitionEntropy] = 0f,
            [BehaviorMetricKeys.StateOccupancy] = 0f,
            [BehaviorMetricKeys.ReadyTimingEntropy] = 0f,
            [BehaviorMetricKeys.ResponseDiversity] = 0f,
            [BehaviorMetricKeys.OccupancySignal] = 0f,
            [BehaviorMetricKeys.AuxiliaryFitness] = 0f,
            [BehaviorMetricKeys.StageGate] = 0f,
            [BehaviorMetricKeys.SelectionSignal] = 0f,
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

    private readonly record struct MultiplicationSurfaceMetrics(
        float SurfaceFitness,
        float Monotonicity,
        float ProductCorrelation,
        float InteriorProximityFitness,
        float WeakSideAccuracy);

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

    private static float ApplyBehaviorAuxiliaryBonus(
        float baseFitness,
        IReadOnlyDictionary<string, float> breakdown)
    {
        var behaviorFitness = breakdown.TryGetValue(BehaviorMetricKeys.SelectionSignal, out var value)
            ? ClampUnitFinite(value)
            : 0f;
        return Math.Clamp(
            baseFitness + (BehaviorFitnessBonusWeight * behaviorFitness * (1f - baseFitness)),
            0f,
            1f);
    }

    private static void AddBehaviorOccupancyMetrics(
        Dictionary<string, float> breakdown,
        IReadOnlyList<DeterministicScalarOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            AddBehaviorOccupancyMetrics(breakdown, BehaviorOccupancyAnalyzer.Empty);
            return;
        }

        var targetProximity = breakdown.TryGetValue("target_proximity_fitness", out var proximityValue)
            ? ClampUnitFinite(proximityValue)
            : 0f;
        var readyConfidence = breakdown.TryGetValue("ready_confidence", out var readyValue)
            ? ClampUnitFinite(readyValue)
            : 1f;
        var samples = outcomes
            .Select(static outcome => new BehaviorOccupancySample(
                outcome.Sample.ExpectedOutput,
                outcome.Observation.OutputValue,
                outcome.Observation.ReadyTickCount))
            .ToArray();
        var metrics = BehaviorOccupancyAnalyzer.Analyze(samples, readyConfidence, targetProximity);
        AddBehaviorOccupancyMetrics(breakdown, metrics);
    }

    private static void ApplyBehaviorStageGate(
        Dictionary<string, float> breakdown,
        float balancedAccuracy,
        bool behaviorOccupancyEnabled,
        float behaviorStageGateStart,
        float behaviorStageGateFull)
    {
        var behaviorEnabled = behaviorOccupancyEnabled;
        var stageGate = behaviorEnabled
            ? BehaviorOccupancyAnalyzer.ResolveStageGate(balancedAccuracy, behaviorStageGateStart, behaviorStageGateFull)
            : 0f;
        var behaviorAuxiliaryFitness = breakdown.TryGetValue(BehaviorMetricKeys.AuxiliaryFitness, out var value)
            ? ClampUnitFinite(value)
            : 0f;
        breakdown[BehaviorMetricKeys.OccupancyEnabled] = behaviorEnabled ? 1f : 0f;
        breakdown[BehaviorMetricKeys.StageGate] = stageGate;
        breakdown[BehaviorMetricKeys.SelectionSignal] = Math.Clamp(behaviorAuxiliaryFitness * stageGate, 0f, 1f);
    }

    private static void AddBehaviorOccupancyMetrics(
        Dictionary<string, float> breakdown,
        BehaviorOccupancyMetrics metrics)
    {
        breakdown[BehaviorMetricKeys.OutputEntropy] = metrics.OutputEntropy;
        breakdown[BehaviorMetricKeys.OccupancyEnabled] = 0f;
        breakdown[BehaviorMetricKeys.TransitionEntropy] = metrics.TransitionEntropy;
        breakdown[BehaviorMetricKeys.StateOccupancy] = metrics.StateOccupancy;
        breakdown[BehaviorMetricKeys.ReadyTimingEntropy] = metrics.ReadyTimingEntropy;
        breakdown[BehaviorMetricKeys.ResponseDiversity] = metrics.ResponseDiversity;
        breakdown[BehaviorMetricKeys.OccupancySignal] = metrics.OccupancySignal;
        breakdown[BehaviorMetricKeys.AuxiliaryFitness] = metrics.AuxiliaryFitness;
        breakdown[BehaviorMetricKeys.StageGate] = 0f;
        breakdown[BehaviorMetricKeys.SelectionSignal] = 0f;
    }

    private static float ClampUnitFinite(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
