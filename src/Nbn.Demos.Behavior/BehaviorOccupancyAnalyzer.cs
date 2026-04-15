namespace Nbn.Demos.Behavior;

public readonly record struct BehaviorOccupancySample(
    float ExpectedValue,
    float ObservedValue,
    float ReadyTickCount);

public sealed record BehaviorOccupancyOptions
{
    public static BehaviorOccupancyOptions Default { get; } = new();

    public int BinCount { get; init; } = 8;
    public float OutputEntropyWeight { get; init; } = 0.45f;
    public float TransitionEntropyWeight { get; init; } = 0.25f;
    public float StateOccupancyWeight { get; init; } = 0.30f;
    public float OccupancySignalWeight { get; init; } = 0.40f;
    public float ResponseDiversityWeight { get; init; } = 0.60f;
    public float TargetProximityViabilityFloor { get; init; } = 0.25f;
}

public readonly record struct BehaviorOccupancyMetrics(
    float OutputEntropy,
    float TransitionEntropy,
    float StateOccupancy,
    float ReadyTimingEntropy,
    float ResponseDiversity,
    float OccupancySignal,
    float AuxiliaryFitness);

public static class BehaviorOccupancyAnalyzer
{
    public static BehaviorOccupancyMetrics Empty { get; } = new();

    public static BehaviorOccupancyMetrics Analyze(
        IReadOnlyList<BehaviorOccupancySample> samples,
        float readyConfidence,
        float targetProximityFitness,
        BehaviorOccupancyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return Empty;
        }

        var effectiveOptions = options ?? BehaviorOccupancyOptions.Default;
        var binCount = Math.Max(2, effectiveOptions.BinCount);
        var outputBins = new int[samples.Count];
        var expectedBins = new int[samples.Count];
        var readyTickBins = new int[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            outputBins[i] = QuantizeUnit(samples[i].ObservedValue, binCount);
            expectedBins[i] = QuantizeUnit(samples[i].ExpectedValue, binCount);
            readyTickBins[i] = QuantizeReadyTick(samples[i].ReadyTickCount, binCount);
        }

        var outputEntropy = ComputeNormalizedEntropy(outputBins, Math.Min(binCount, samples.Count));
        var transitionEntropy = ComputeTransitionEntropy(outputBins, binCount);
        var stateOccupancy = ComputeStateOccupancy(outputBins, binCount);
        var readyTimingEntropy = ComputeNormalizedEntropy(readyTickBins, Math.Min(binCount, samples.Count));
        var responseDiversity = ComputeNormalizedMutualInformation(expectedBins, outputBins, binCount);
        var viabilityGate = ClampUnitFinite(readyConfidence)
                            * (ClampUnitFinite(effectiveOptions.TargetProximityViabilityFloor)
                               + ((1f - ClampUnitFinite(effectiveOptions.TargetProximityViabilityFloor))
                                  * ClampUnitFinite(targetProximityFitness)));
        var rawOccupancy = ClampUnitFinite(
            (effectiveOptions.OutputEntropyWeight * outputEntropy)
            + (effectiveOptions.TransitionEntropyWeight * transitionEntropy)
            + (effectiveOptions.StateOccupancyWeight * stateOccupancy));
        var occupancySignal = ClampUnitFinite(rawOccupancy * viabilityGate);
        var controllableSignal = ClampUnitFinite(responseDiversity * viabilityGate);
        var auxiliaryFitness = ClampUnitFinite(
            (effectiveOptions.OccupancySignalWeight * occupancySignal)
            + (effectiveOptions.ResponseDiversityWeight * controllableSignal));

        return new BehaviorOccupancyMetrics(
            OutputEntropy: outputEntropy,
            TransitionEntropy: transitionEntropy,
            StateOccupancy: stateOccupancy,
            ReadyTimingEntropy: readyTimingEntropy,
            ResponseDiversity: responseDiversity,
            OccupancySignal: occupancySignal,
            AuxiliaryFitness: auxiliaryFitness);
    }

    public static float ResolveStageGate(float score, float gateStart, float gateFull)
    {
        if (!float.IsFinite(gateStart)
            || !float.IsFinite(gateFull))
        {
            return 0f;
        }

        var start = ClampUnitFinite(gateStart);
        var full = ClampUnitFinite(gateFull);
        if (full <= start)
        {
            return 0f;
        }

        var normalized = ClampUnitFinite((ClampUnitFinite(score) - start) / (full - start));
        return normalized * normalized * (3f - (2f * normalized));
    }

    private static int QuantizeUnit(float value, int binCount)
    {
        var clamped = ClampUnitFinite(value);
        return Math.Clamp((int)MathF.Floor(clamped * binCount), 0, binCount - 1);
    }

    private static int QuantizeReadyTick(float value, int binCount)
    {
        if (!float.IsFinite(value) || value <= 0f)
        {
            return 0;
        }

        return Math.Clamp((int)MathF.Floor(value), 0, binCount - 1);
    }

    private static float ComputeStateOccupancy(IReadOnlyList<int> bins, int binCount)
    {
        if (bins.Count == 0)
        {
            return 0f;
        }

        var maxDistinctCount = Math.Min(binCount, bins.Count);
        if (maxDistinctCount <= 1)
        {
            return 0f;
        }

        var distinctCount = bins.Distinct().Count();
        return ClampUnitFinite((distinctCount - 1) / (float)(maxDistinctCount - 1));
    }

    private static float ComputeTransitionEntropy(IReadOnlyList<int> bins, int binCount)
    {
        if (bins.Count <= 1)
        {
            return 0f;
        }

        var transitions = new int[bins.Count - 1];
        for (var i = 1; i < bins.Count; i++)
        {
            transitions[i - 1] = (bins[i - 1] * binCount) + bins[i];
        }

        return ComputeNormalizedEntropy(
            transitions,
            Math.Min(binCount * binCount, transitions.Length));
    }

    private static float ComputeNormalizedMutualInformation(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right,
        int binCount)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return 0f;
        }

        var leftEntropy = ComputeEntropy(left);
        var rightEntropy = ComputeEntropy(right);
        var normalizer = Math.Min(leftEntropy, rightEntropy);
        if (normalizer <= 0d)
        {
            return 0f;
        }

        var joint = new int[left.Count];
        for (var i = 0; i < left.Count; i++)
        {
            joint[i] = (left[i] * binCount) + right[i];
        }

        var mutualInformation = Math.Max(0d, leftEntropy + rightEntropy - ComputeEntropy(joint));
        return ClampUnitFinite((float)(mutualInformation / normalizer));
    }

    private static float ComputeNormalizedEntropy(
        IReadOnlyList<int> values,
        int maxDistinctCount)
    {
        if (values.Count <= 1 || maxDistinctCount <= 1)
        {
            return 0f;
        }

        var entropy = ComputeEntropy(values);
        var maxEntropy = Math.Log(maxDistinctCount);
        return maxEntropy <= 0d
            ? 0f
            : ClampUnitFinite((float)(entropy / maxEntropy));
    }

    private static double ComputeEntropy(IReadOnlyList<int> values)
    {
        if (values.Count <= 1)
        {
            return 0d;
        }

        var counts = new Dictionary<int, int>();
        foreach (var value in values)
        {
            counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
        }

        var entropy = 0d;
        foreach (var count in counts.Values)
        {
            var probability = count / (double)values.Count;
            entropy -= probability * Math.Log(probability);
        }

        return entropy;
    }

    private static float ClampUnitFinite(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
