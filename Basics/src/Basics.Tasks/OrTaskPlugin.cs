using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

public sealed class OrTaskPlugin : BooleanDatasetTaskPluginBase
{
    private static readonly IReadOnlyList<BasicsTaskSample> Dataset =
    [
        new BasicsTaskSample(InputA: 0f, InputB: 0f, ExpectedOutput: 0f, Label: "00"),
        new BasicsTaskSample(InputA: 0f, InputB: 1f, ExpectedOutput: 1f, Label: "01"),
        new BasicsTaskSample(InputA: 1f, InputB: 0f, ExpectedOutput: 1f, Label: "10"),
        new BasicsTaskSample(InputA: 1f, InputB: 1f, ExpectedOutput: 1f, Label: "11")
    ];

    public OrTaskPlugin()
        : base(
            taskId: "or",
            displayName: "OR",
            description: "Boolean OR over the full deterministic 0/1 truth table.",
            dataset: Dataset,
            coverageKey: "truth_table_coverage")
    {
    }
}
