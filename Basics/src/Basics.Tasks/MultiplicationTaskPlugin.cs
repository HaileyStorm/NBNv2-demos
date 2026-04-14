using Nbn.Demos.Basics.Environment;
using System.Globalization;

namespace Nbn.Demos.Basics.Tasks;

public sealed class MultiplicationTaskPlugin : IBasicsTaskPlugin
{
    private readonly IReadOnlyList<BasicsTaskSample> _dataset;
    private readonly float _accuracyTolerance;

    public BasicsTaskContract Contract { get; } = new(
        TaskId: "multiplication",
        DisplayName: "Multiplication",
        InputWidth: BasicsIoGeometry.InputWidth,
        OutputWidth: BasicsIoGeometry.OutputWidth,
        UsesTickAlignedEvaluation: true,
        Description: "Bounded scalar multiplication over a deterministic stratified grid in [0,1], keeping all interior samples and a capped boundary subset, with normalized output equal to a*b and tolerance accuracy measured at the configured tolerance.");

    public MultiplicationTaskPlugin(BasicsMultiplicationTaskSettings? settings = null)
    {
        var effectiveSettings = settings ?? new BasicsMultiplicationTaskSettings();
        _dataset = CreateDataset(effectiveSettings.UniqueInputValueCount);
        _accuracyTolerance = effectiveSettings.AccuracyTolerance;
    }

    public IReadOnlyList<BasicsTaskSample> BuildDeterministicDataset() => _dataset;

    public BasicsTaskEvaluationResult Evaluate(
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations)
        => BasicsTaskPluginScoring.EvaluateMultiplicationDataset(
            Contract,
            _dataset,
            context,
            samples,
            observations,
            coverageKey: "evaluation_set_coverage",
            accuracyTolerance: _accuracyTolerance);

    private static IReadOnlyList<BasicsTaskSample> CreateDataset(int uniqueInputValueCount)
    {
        var values = Enumerable.Range(0, uniqueInputValueCount)
            .Select(index => uniqueInputValueCount == 1 ? 0f : index / (uniqueInputValueCount - 1f))
            .ToArray();
        var selectedEdgeCoordinates = ResolveSelectedEdgeCoordinates(uniqueInputValueCount);
        var dataset = new List<BasicsTaskSample>(ResolveDatasetCapacity(uniqueInputValueCount, selectedEdgeCoordinates.Count));
        for (var inputAIndex = 0; inputAIndex < values.Length; inputAIndex++)
        {
            for (var inputBIndex = 0; inputBIndex < values.Length; inputBIndex++)
            {
                if (!ShouldIncludeCoordinate(inputAIndex, inputBIndex, uniqueInputValueCount, selectedEdgeCoordinates))
                {
                    continue;
                }

                var inputA = values[inputAIndex];
                var inputB = values[inputBIndex];
                dataset.Add(new BasicsTaskSample(
                    inputA,
                    inputB,
                    inputA * inputB,
                    Label: $"{inputA.ToString("0.00", CultureInfo.InvariantCulture)}x{inputB.ToString("0.00", CultureInfo.InvariantCulture)}"));
            }
        }

        return dataset;
    }

    private static int ResolveDatasetCapacity(int uniqueInputValueCount, int selectedEdgeCount)
    {
        var interiorAxisCount = Math.Max(0, uniqueInputValueCount - 2);
        return selectedEdgeCount + (interiorAxisCount * interiorAxisCount);
    }

    private static bool ShouldIncludeCoordinate(
        int inputAIndex,
        int inputBIndex,
        int uniqueInputValueCount,
        IReadOnlySet<(int InputAIndex, int InputBIndex)> selectedEdgeCoordinates)
    {
        if (uniqueInputValueCount <= 2)
        {
            return true;
        }

        var isEdge = inputAIndex is 0 || inputBIndex is 0
            || inputAIndex == uniqueInputValueCount - 1
            || inputBIndex == uniqueInputValueCount - 1;
        return !isEdge || selectedEdgeCoordinates.Contains((inputAIndex, inputBIndex));
    }

    private static IReadOnlySet<(int InputAIndex, int InputBIndex)> ResolveSelectedEdgeCoordinates(int uniqueInputValueCount)
    {
        var orderedPerimeter = BuildOrderedPerimeter(uniqueInputValueCount);
        if (orderedPerimeter.Count == 0)
        {
            return new HashSet<(int, int)>();
        }

        if (uniqueInputValueCount <= 2)
        {
            return orderedPerimeter.ToHashSet();
        }

        var interiorAxisCount = Math.Max(0, uniqueInputValueCount - 2);
        var interiorCount = interiorAxisCount * interiorAxisCount;
        if (interiorCount == 0)
        {
            return orderedPerimeter.ToHashSet();
        }

        var targetEdgeCount = Math.Min(orderedPerimeter.Count, Math.Max(4, interiorCount));
        if (targetEdgeCount >= orderedPerimeter.Count)
        {
            return orderedPerimeter.ToHashSet();
        }

        var selected = new HashSet<(int InputAIndex, int InputBIndex)>
        {
            (0, 0),
            (0, uniqueInputValueCount - 1),
            (uniqueInputValueCount - 1, 0),
            (uniqueInputValueCount - 1, uniqueInputValueCount - 1)
        };
        if (selected.Count >= targetEdgeCount)
        {
            return selected;
        }

        var remainingCandidates = orderedPerimeter
            .Where(candidate => !selected.Contains(candidate))
            .ToArray();
        var remainingTargetCount = targetEdgeCount - selected.Count;
        if (remainingTargetCount >= remainingCandidates.Length)
        {
            foreach (var candidate in remainingCandidates)
            {
                selected.Add(candidate);
            }

            return selected;
        }

        for (var slot = 0; slot < remainingTargetCount; slot++)
        {
            var scaledIndex = (slot * (remainingCandidates.Length - 1d)) / Math.Max(1d, remainingTargetCount - 1d);
            var candidate = remainingCandidates[(int)Math.Round(scaledIndex, MidpointRounding.AwayFromZero)];
            selected.Add(candidate);
        }

        if (selected.Count < targetEdgeCount)
        {
            foreach (var candidate in remainingCandidates)
            {
                if (selected.Add(candidate) && selected.Count >= targetEdgeCount)
                {
                    break;
                }
            }
        }

        return selected;
    }

    private static List<(int InputAIndex, int InputBIndex)> BuildOrderedPerimeter(int uniqueInputValueCount)
    {
        var perimeter = new List<(int InputAIndex, int InputBIndex)>();
        if (uniqueInputValueCount <= 0)
        {
            return perimeter;
        }

        if (uniqueInputValueCount == 1)
        {
            perimeter.Add((0, 0));
            return perimeter;
        }

        for (var inputBIndex = 0; inputBIndex < uniqueInputValueCount; inputBIndex++)
        {
            perimeter.Add((0, inputBIndex));
        }

        for (var inputAIndex = 1; inputAIndex < uniqueInputValueCount - 1; inputAIndex++)
        {
            perimeter.Add((inputAIndex, uniqueInputValueCount - 1));
        }

        for (var inputBIndex = uniqueInputValueCount - 1; inputBIndex >= 0; inputBIndex--)
        {
            perimeter.Add((uniqueInputValueCount - 1, inputBIndex));
        }

        for (var inputAIndex = uniqueInputValueCount - 2; inputAIndex >= 1; inputAIndex--)
        {
            perimeter.Add((inputAIndex, 0));
        }

        return perimeter;
    }
}
