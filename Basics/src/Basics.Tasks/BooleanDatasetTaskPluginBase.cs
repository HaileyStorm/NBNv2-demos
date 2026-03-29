using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

public abstract class BooleanDatasetTaskPluginBase : IBasicsTaskPlugin
{
    private readonly IReadOnlyList<BasicsTaskSample> _dataset;
    private readonly string _coverageKey;

    protected BooleanDatasetTaskPluginBase(
        string taskId,
        string displayName,
        string description,
        IReadOnlyList<BasicsTaskSample> dataset,
        string coverageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(coverageKey);

        _dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        _coverageKey = coverageKey;
        Contract = new BasicsTaskContract(
            TaskId: taskId,
            DisplayName: displayName,
            InputWidth: BasicsIoGeometry.InputWidth,
            OutputWidth: BasicsIoGeometry.OutputWidth,
            UsesTickAlignedEvaluation: true,
            Description: description ?? string.Empty);
    }

    public BasicsTaskContract Contract { get; }

    public IReadOnlyList<BasicsTaskSample> BuildDeterministicDataset() => _dataset;

    public BasicsTaskEvaluationResult Evaluate(
        BasicsTaskEvaluationContext context,
        IReadOnlyList<BasicsTaskSample> samples,
        IReadOnlyList<BasicsTaskObservation> observations)
        => BasicsTaskPluginScoring.EvaluateBooleanDataset(
            Contract,
            _dataset,
            context,
            samples,
            observations,
            _coverageKey);
}
