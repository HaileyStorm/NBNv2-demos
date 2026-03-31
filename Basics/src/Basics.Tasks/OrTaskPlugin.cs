using Nbn.Demos.Basics.Environment;
using System.Globalization;

namespace Nbn.Demos.Basics.Tasks;

public sealed class OrTaskPlugin : BooleanDatasetTaskPluginBase
{
    public OrTaskPlugin(BasicsBinaryTruthTableTaskSettings? settings = null)
        : base(
            taskId: "or",
            displayName: "OR",
            description: "Boolean OR over the full deterministic 0/1 truth table.",
            dataset: CreateDataset(settings ?? new BasicsBinaryTruthTableTaskSettings()),
            coverageKey: "truth_table_coverage")
    {
    }

    private static IReadOnlyList<BasicsTaskSample> CreateDataset(BasicsBinaryTruthTableTaskSettings settings)
    {
        var low = settings.LowInputValue;
        var high = settings.HighInputValue;
        return
        [
            new BasicsTaskSample(InputA: low, InputB: low, ExpectedOutput: 0f, Label: FormatLabel(low, low)),
            new BasicsTaskSample(InputA: low, InputB: high, ExpectedOutput: 1f, Label: FormatLabel(low, high)),
            new BasicsTaskSample(InputA: high, InputB: low, ExpectedOutput: 1f, Label: FormatLabel(high, low)),
            new BasicsTaskSample(InputA: high, InputB: high, ExpectedOutput: 1f, Label: FormatLabel(high, high))
        ];
    }

    private static string FormatLabel(float inputA, float inputB)
        => $"{inputA.ToString("0.##", CultureInfo.InvariantCulture)},{inputB.ToString("0.##", CultureInfo.InvariantCulture)}";
}
