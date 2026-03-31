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
        Description: "Bounded scalar multiplication over the full 5x5 grid in [0,1], with normalized output equal to a*b and tolerance accuracy measured at +/-0.05.");

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
        => BasicsTaskPluginScoring.EvaluateBoundedRegressionDataset(
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
        var dataset = new List<BasicsTaskSample>(values.Length * values.Length);
        foreach (var inputA in values)
        {
            foreach (var inputB in values)
            {
                dataset.Add(new BasicsTaskSample(
                    inputA,
                    inputB,
                    inputA * inputB,
                    Label: $"{inputA.ToString("0.00", CultureInfo.InvariantCulture)}x{inputB.ToString("0.00", CultureInfo.InvariantCulture)}"));
            }
        }

        return dataset;
    }
}
