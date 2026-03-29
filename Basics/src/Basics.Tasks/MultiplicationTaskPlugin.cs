using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

public sealed class MultiplicationTaskPlugin : IBasicsTaskPlugin
{
    private const float AccuracyTolerance = 0.05f;

    private static readonly IReadOnlyList<BasicsTaskSample> Dataset =
    [
        new BasicsTaskSample(0f, 0f, 0f, Label: "0.00x0.00"),
        new BasicsTaskSample(0f, 0.25f, 0f, Label: "0.00x0.25"),
        new BasicsTaskSample(0f, 0.5f, 0f, Label: "0.00x0.50"),
        new BasicsTaskSample(0f, 0.75f, 0f, Label: "0.00x0.75"),
        new BasicsTaskSample(0f, 1f, 0f, Label: "0.00x1.00"),
        new BasicsTaskSample(0.25f, 0f, 0f, Label: "0.25x0.00"),
        new BasicsTaskSample(0.25f, 0.25f, 0.0625f, Label: "0.25x0.25"),
        new BasicsTaskSample(0.25f, 0.5f, 0.125f, Label: "0.25x0.50"),
        new BasicsTaskSample(0.25f, 0.75f, 0.1875f, Label: "0.25x0.75"),
        new BasicsTaskSample(0.25f, 1f, 0.25f, Label: "0.25x1.00"),
        new BasicsTaskSample(0.5f, 0f, 0f, Label: "0.50x0.00"),
        new BasicsTaskSample(0.5f, 0.25f, 0.125f, Label: "0.50x0.25"),
        new BasicsTaskSample(0.5f, 0.5f, 0.25f, Label: "0.50x0.50"),
        new BasicsTaskSample(0.5f, 0.75f, 0.375f, Label: "0.50x0.75"),
        new BasicsTaskSample(0.5f, 1f, 0.5f, Label: "0.50x1.00"),
        new BasicsTaskSample(0.75f, 0f, 0f, Label: "0.75x0.00"),
        new BasicsTaskSample(0.75f, 0.25f, 0.1875f, Label: "0.75x0.25"),
        new BasicsTaskSample(0.75f, 0.5f, 0.375f, Label: "0.75x0.50"),
        new BasicsTaskSample(0.75f, 0.75f, 0.5625f, Label: "0.75x0.75"),
        new BasicsTaskSample(0.75f, 1f, 0.75f, Label: "0.75x1.00"),
        new BasicsTaskSample(1f, 0f, 0f, Label: "1.00x0.00"),
        new BasicsTaskSample(1f, 0.25f, 0.25f, Label: "1.00x0.25"),
        new BasicsTaskSample(1f, 0.5f, 0.5f, Label: "1.00x0.50"),
        new BasicsTaskSample(1f, 0.75f, 0.75f, Label: "1.00x0.75"),
        new BasicsTaskSample(1f, 1f, 1f, Label: "1.00x1.00")
    ];

    public BasicsTaskContract Contract { get; } = new(
        TaskId: "multiplication",
        DisplayName: "Multiplication",
        InputWidth: BasicsIoGeometry.InputWidth,
        OutputWidth: BasicsIoGeometry.OutputWidth,
        UsesTickAlignedEvaluation: true,
        Description: "Bounded scalar multiplication over the full 5x5 grid in [0,1], with normalized output equal to a*b and tolerance accuracy measured at +/-0.05.");

    public IReadOnlyList<BasicsTaskSample> BuildDeterministicDataset() => Dataset;

    public BasicsTaskEvaluationResult Evaluate(
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations)
        => BasicsTaskPluginScoring.EvaluateBoundedRegressionDataset(
            Contract,
            Dataset,
            context,
            samples,
            observations,
            coverageKey: "evaluation_set_coverage",
            accuracyTolerance: AccuracyTolerance);
}
