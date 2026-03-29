using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

public sealed class GtTaskPlugin : BooleanDatasetTaskPluginBase
{
    private static readonly IReadOnlyList<BasicsTaskSample> Dataset =
    [
        new BasicsTaskSample(InputA: 0f, InputB: 0f, ExpectedOutput: 0f, Label: "0.00>0.00"),
        new BasicsTaskSample(InputA: 0f, InputB: 0.5f, ExpectedOutput: 0f, Label: "0.00>0.50"),
        new BasicsTaskSample(InputA: 0f, InputB: 1f, ExpectedOutput: 0f, Label: "0.00>1.00"),
        new BasicsTaskSample(InputA: 0.5f, InputB: 0f, ExpectedOutput: 1f, Label: "0.50>0.00"),
        new BasicsTaskSample(InputA: 0.5f, InputB: 0.5f, ExpectedOutput: 0f, Label: "0.50>0.50"),
        new BasicsTaskSample(InputA: 0.5f, InputB: 1f, ExpectedOutput: 0f, Label: "0.50>1.00"),
        new BasicsTaskSample(InputA: 1f, InputB: 0f, ExpectedOutput: 1f, Label: "1.00>0.00"),
        new BasicsTaskSample(InputA: 1f, InputB: 0.5f, ExpectedOutput: 1f, Label: "1.00>0.50"),
        new BasicsTaskSample(InputA: 1f, InputB: 1f, ExpectedOutput: 0f, Label: "1.00>1.00")
    ];

    public GtTaskPlugin()
        : base(
            taskId: "gt",
            displayName: "GT",
            description: "Bounded scalar greater-than over the full 3x3 comparison grid in [0,1], with output 1 when a > b and 0 otherwise.",
            dataset: Dataset,
            coverageKey: "comparison_set_coverage")
    {
    }
}
