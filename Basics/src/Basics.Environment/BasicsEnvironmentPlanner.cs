using Nbn.Proto.Io;

namespace Nbn.Demos.Basics.Environment;

public sealed class BasicsEnvironmentPlanner
{
    private readonly IBasicsRuntimeClient _runtimeClient;

    public BasicsEnvironmentPlanner(IBasicsRuntimeClient runtimeClient)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
    }

    public async Task<BasicsEnvironmentPlan> BuildPlanAsync(
        BasicsEnvironmentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = options.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        }

        var placementInventory = await _runtimeClient.GetPlacementWorkerInventoryAsync(cancellationToken).ConfigureAwait(false);
        var capacity = BasicsCapacitySizer.Recommend(placementInventory, options.SizingOverrides);
        return new BasicsEnvironmentPlan(
            SelectedTask: options.SelectedTask,
            SeedTemplate: options.SeedTemplate,
            SizingOverrides: options.SizingOverrides,
            InitialBrainSeeds: options.InitialBrainSeeds.ToArray(),
            Capacity: capacity,
            OutputObservationMode: options.OutputObservationMode,
            OutputSamplingPolicy: options.OutputSamplingPolicy,
            DiversityPreset: options.DiversityPreset,
            AdaptiveDiversity: options.AdaptiveDiversity,
            Reproduction: options.Reproduction,
            Scheduling: options.Scheduling,
            Metrics: options.Metrics,
            StopCriteria: options.StopCriteria,
            PlannedAtUtc: DateTimeOffset.UtcNow,
            TaskSettings: options.TaskSettings);
    }

    public async Task<BasicsBrainGeometryValidation> ValidateBrainGeometryAsync(
        Guid brainId,
        CancellationToken cancellationToken = default)
    {
        if (brainId == Guid.Empty)
        {
            return new BasicsBrainGeometryValidation(
                IsValid: false,
                ExpectedInputWidth: BasicsIoGeometry.InputWidth,
                ExpectedOutputWidth: BasicsIoGeometry.OutputWidth,
                ActualInputWidth: 0,
                ActualOutputWidth: 0,
                FailureReason: "brain_id_missing");
        }

        var info = await _runtimeClient.RequestBrainInfoAsync(brainId, cancellationToken).ConfigureAwait(false);
        return BasicsIoGeometry.Validate(info);
    }
}
