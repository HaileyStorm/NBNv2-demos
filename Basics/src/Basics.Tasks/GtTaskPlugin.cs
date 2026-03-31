using Nbn.Demos.Basics.Environment;
using System.Globalization;

namespace Nbn.Demos.Basics.Tasks;

public sealed class GtTaskPlugin : BooleanDatasetTaskPluginBase
{
    public GtTaskPlugin(BasicsScalarGridTaskSettings? settings = null)
        : base(
            taskId: "gt",
            displayName: "GT",
            description: "Bounded scalar greater-than over the full 3x3 comparison grid in [0,1], with output 1 when a > b and 0 otherwise.",
            dataset: CreateDataset(settings ?? new BasicsScalarGridTaskSettings()),
            coverageKey: "comparison_set_coverage")
    {
    }

    private static IReadOnlyList<BasicsTaskSample> CreateDataset(BasicsScalarGridTaskSettings settings)
    {
        var values = BuildValues(settings.UniqueInputValueCount);
        var dataset = new List<BasicsTaskSample>(values.Length * values.Length);
        foreach (var inputA in values)
        {
            foreach (var inputB in values)
            {
                dataset.Add(new BasicsTaskSample(
                    InputA: inputA,
                    InputB: inputB,
                    ExpectedOutput: inputA > inputB ? 1f : 0f,
                    Label: $"{inputA.ToString("0.00", CultureInfo.InvariantCulture)}>{inputB.ToString("0.00", CultureInfo.InvariantCulture)}"));
            }
        }

        return dataset;
    }

    private static float[] BuildValues(int count)
        => Enumerable.Range(0, count)
            .Select(index => count == 1 ? 0f : index / (count - 1f))
            .ToArray();
}
