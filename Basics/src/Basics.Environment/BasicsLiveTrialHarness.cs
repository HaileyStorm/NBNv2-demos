using System.Diagnostics;

namespace Nbn.Demos.Basics.Environment;

public enum BasicsLiveTrialOutcome
{
    Succeeded = 0,
    TimedOut = 1,
    Failed = 2,
    Stopped = 3,
    ConnectFailed = 4,
    PlanningFailed = 5,
    RuntimeClientFailed = 6
}

public enum BasicsLiveTrialPhase
{
    StartingTrial = 0,
    Connecting = 1,
    Planning = 2,
    Running = 3,
    Completed = 4,
    Tuning = 5,
    Finished = 6
}

public sealed record BasicsLiveTrialStabilityCriteria
{
    public float TargetAccuracy { get; init; } = 1f;
    public float TargetFitness { get; init; } = 0.99f;
    public int RequiredSuccessfulTrials { get; init; } = 2;

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (!float.IsFinite(TargetAccuracy) || TargetAccuracy < 0f || TargetAccuracy > 1f)
        {
            errors.Add("TargetAccuracy must be a finite value between 0 and 1.");
        }

        if (!float.IsFinite(TargetFitness) || TargetFitness < 0f || TargetFitness > 1f)
        {
            errors.Add("TargetFitness must be a finite value between 0 and 1.");
        }

        if (RequiredSuccessfulTrials <= 0)
        {
            errors.Add("RequiredSuccessfulTrials must be > 0.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public sealed record BasicsLiveAutoTuningOptions
{
    public bool Enabled { get; init; } = true;
    public bool PreferVectorPotentialOnFailures { get; init; } = true;
    public bool ReduceSizingOnFailures { get; init; } = true;
}

public sealed record BasicsLiveTrialHarnessOptions
{
    public required BasicsRuntimeClientOptions RuntimeClient { get; init; }
    public required BasicsEnvironmentOptions Environment { get; init; }
    public BasicsTemplatePublishingOptions TemplatePublishing { get; init; } = new();
    public int MaxTrialCount { get; init; } = 4;
    public TimeSpan TrialTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public BasicsLiveTrialStabilityCriteria StabilityCriteria { get; init; } = new();
    public BasicsLiveAutoTuningOptions AutoTuning { get; init; } = new();
    public string RunLabel { get; init; } = "basics-live";

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        AddValidationErrors(Environment.Validate(), errors);
        AddValidationErrors(StabilityCriteria.Validate(), errors);
        if (MaxTrialCount <= 0)
        {
            errors.Add("MaxTrialCount must be > 0.");
        }

        if (TrialTimeout <= TimeSpan.Zero)
        {
            errors.Add("TrialTimeout must be > 0.");
        }

        if (StabilityCriteria.RequiredSuccessfulTrials > MaxTrialCount)
        {
            errors.Add("RequiredSuccessfulTrials must be <= MaxTrialCount.");
        }

        if (string.IsNullOrWhiteSpace(RunLabel))
        {
            errors.Add("RunLabel is required.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }

    private static void AddValidationErrors(BasicsContractValidationResult validation, ICollection<string> errors)
    {
        if (validation.IsValid)
        {
            return;
        }

        foreach (var error in validation.Errors)
        {
            errors.Add(error);
        }
    }
}

public sealed record BasicsLiveTrialSeedShapeSnapshot(
    int? MinActiveInternalRegionCount,
    int? MaxActiveInternalRegionCount,
    int? MinInternalNeuronCount,
    int? MaxInternalNeuronCount,
    int? MinAxonCount,
    int? MaxAxonCount);

public sealed record BasicsLiveTrialVariationBandSnapshot(
    int MaxInternalNeuronDelta,
    int MaxAxonDelta,
    int MaxStrengthCodeDelta,
    int MaxParameterCodeDelta,
    bool AllowFunctionMutation,
    bool AllowAxonReroute,
    bool AllowRegionSetChange);

public sealed record BasicsLiveTrialSizingSnapshot(
    int? InitialPopulationCount,
    uint? ReproductionRunCount,
    int? MaxConcurrentBrains);

public sealed record BasicsLiveTrialSchedulingSnapshot(
    double FitnessWeight,
    double DiversityWeight,
    double SpeciesBalanceWeight,
    double EliteFraction,
    double ExplorationFraction,
    int MaxParentsPerSpecies,
    uint MinRunsPerPair,
    uint MaxRunsPerPair,
    double FitnessExponent,
    double DiversityBoost);

public sealed record BasicsLiveTrialConfigurationSnapshot(
    string ClientName,
    string TaskId,
    string TaskDisplayName,
    string TemplateId,
    string TemplateDescription,
    BasicsOutputObservationMode OutputObservationMode,
    BasicsLiveTrialVariationBandSnapshot VariationBand,
    BasicsLiveTrialSeedShapeSnapshot SeedShape,
    BasicsLiveTrialSizingSnapshot Sizing,
    BasicsLiveTrialSchedulingSnapshot Scheduling);

public sealed record BasicsLiveTrialPlanSummary(
    string TaskId,
    string TaskDisplayName,
    string TemplateId,
    BasicsOutputObservationMode OutputObservationMode,
    int RecommendedInitialPopulationCount,
    uint RecommendedReproductionRunCount,
    int RecommendedMaxConcurrentBrains,
    int EligibleWorkerCount,
    float CapacityScore,
    string CapacitySummary,
    DateTimeOffset PlannedAtUtc);

public sealed record BasicsLiveTrialBestCandidateRecord(
    string ArtifactSha256,
    string SpeciesId,
    float Accuracy,
    float Fitness,
    IReadOnlyDictionary<string, float> ScoreBreakdown,
    IReadOnlyList<string> Diagnostics);

public sealed record BasicsLiveTrialSnapshotRecord(
    DateTimeOffset ObservedAtUtc,
    BasicsExecutionState State,
    string StatusText,
    string DetailText,
    ulong? SpeciationEpochId,
    int EvaluationFailureCount,
    string EvaluationFailureSummary,
    int Generation,
    int PopulationCount,
    int ActiveBrainCount,
    int SpeciesCount,
    ulong ReproductionCalls,
    ulong ReproductionRunsObserved,
    float CapacityUtilization,
    float BestAccuracy,
    float BestFitness,
    float MeanFitness,
    BasicsLiveTrialBestCandidateRecord? BestCandidate,
    IReadOnlyList<float> AccuracyHistory,
    IReadOnlyList<float> BestFitnessHistory);

public sealed record BasicsLiveTuningDecision(
    bool Applied,
    string Reason,
    IReadOnlyList<string> Changes,
    BasicsLiveTrialConfigurationSnapshot? NextConfiguration);

public sealed record BasicsLiveTrialResult(
    int TrialNumber,
    BasicsLiveTrialOutcome Outcome,
    string OutcomeDetail,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double DurationSeconds,
    BasicsLiveTrialConfigurationSnapshot AppliedConfiguration,
    BasicsLiveTrialPlanSummary? PlanSummary,
    BasicsLiveTrialSnapshotRecord? TerminalSnapshot,
    int SnapshotCount,
    IReadOnlyList<BasicsLiveTrialSnapshotRecord> Snapshots,
    bool MeetsStabilityCriteria,
    int SuccessfulTrialStreakAfterCompletion,
    BasicsLiveTuningDecision? TuningDecision);

public sealed record BasicsLiveTrialReport(
    string RunLabel,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool StabilityTargetMet,
    int ExecutedTrialCount,
    BasicsLiveTrialStabilityCriteria StabilityCriteria,
    BasicsLiveTrialConfigurationSnapshot InitialConfiguration,
    BasicsLiveTrialConfigurationSnapshot FinalConfiguration,
    IReadOnlyList<BasicsLiveTrialResult> Trials);

public sealed record BasicsLiveTrialProgress(
    int TrialNumber,
    BasicsLiveTrialPhase Phase,
    string Message,
    BasicsLiveTrialSnapshotRecord? Snapshot = null,
    BasicsLiveTuningDecision? TuningDecision = null);

public sealed class BasicsLiveTrialHarness
{
    private readonly Func<BasicsRuntimeClientOptions, CancellationToken, Task<IBasicsRuntimeClient>> _runtimeClientFactory;
    private readonly Func<IBasicsRuntimeClient, BasicsTemplatePublishingOptions, IBasicsExecutionRunner> _executionRunnerFactory;

    public BasicsLiveTrialHarness(
        Func<BasicsRuntimeClientOptions, CancellationToken, Task<IBasicsRuntimeClient>>? runtimeClientFactory = null,
        Func<IBasicsRuntimeClient, BasicsTemplatePublishingOptions, IBasicsExecutionRunner>? executionRunnerFactory = null)
    {
        _runtimeClientFactory = runtimeClientFactory is null
            ? async (options, cancellationToken) => await BasicsRuntimeClient.StartAsync(options, cancellationToken).ConfigureAwait(false)
            : runtimeClientFactory;
        _executionRunnerFactory = executionRunnerFactory is null
            ? (runtimeClient, publishingOptions) => new BasicsExecutionSession(runtimeClient, publishingOptions)
            : executionRunnerFactory;
    }

    public async Task<BasicsLiveTrialReport> RunAsync(
        BasicsLiveTrialHarnessOptions options,
        IBasicsTaskPlugin taskPlugin,
        Action<BasicsLiveTrialProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(taskPlugin);

        var validation = options.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        }

        if (!string.Equals(options.Environment.SelectedTask.TaskId, taskPlugin.Contract.TaskId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Harness task mismatch. Environment requested '{options.Environment.SelectedTask.TaskId}' but plugin '{taskPlugin.Contract.TaskId}' was supplied.");
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var initialConfiguration = CreateConfigurationSnapshot(options.Environment);
        var currentEnvironment = options.Environment;
        var successfulTrialStreak = 0;
        var results = new List<BasicsLiveTrialResult>();

        for (var trialNumber = 1; trialNumber <= options.MaxTrialCount; trialNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trialStartedAtUtc = DateTimeOffset.UtcNow;
            var trialEnvironment = currentEnvironment with
            {
                ClientName = $"{currentEnvironment.ClientName}.trial{trialNumber:D2}"
            };
            var trialConfiguration = CreateConfigurationSnapshot(trialEnvironment);

            onProgress?.Invoke(new BasicsLiveTrialProgress(
                trialNumber,
                BasicsLiveTrialPhase.StartingTrial,
                $"Starting trial {trialNumber}/{options.MaxTrialCount} with output mode {trialEnvironment.OutputObservationMode}."));

            using var timeoutCts = new CancellationTokenSource(options.TrialTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var stopwatch = Stopwatch.StartNew();
            BasicsEnvironmentPlan? plan = null;
            BasicsLiveTrialSnapshotRecord? terminalSnapshot = null;
            var snapshotRecords = new List<BasicsLiveTrialSnapshotRecord>();
            string outcomeDetail;
            BasicsLiveTrialOutcome outcome;
            BasicsLiveTuningDecision? tuningDecision = null;

            try
            {
                await using var runtimeClient = await _runtimeClientFactory(options.RuntimeClient, linkedCts.Token).ConfigureAwait(false);

                onProgress?.Invoke(new BasicsLiveTrialProgress(
                    trialNumber,
                    BasicsLiveTrialPhase.Connecting,
                    $"Connecting trial {trialNumber} to {options.RuntimeClient.IoAddress}/{options.RuntimeClient.IoGatewayName}."));

                var connectAck = await runtimeClient.ConnectAsync(trialEnvironment.ClientName, linkedCts.Token).ConfigureAwait(false);
                if (connectAck is null)
                {
                    outcome = BasicsLiveTrialOutcome.ConnectFailed;
                    outcomeDetail = "connect_failed";
                    successfulTrialStreak = 0;
                }
                else
                {
                    onProgress?.Invoke(new BasicsLiveTrialProgress(
                        trialNumber,
                        BasicsLiveTrialPhase.Planning,
                        $"Building plan for trial {trialNumber} using IO-reported capacity."));

                    var planner = new BasicsEnvironmentPlanner(runtimeClient);
                    plan = await planner.BuildPlanAsync(trialEnvironment, linkedCts.Token).ConfigureAwait(false);
                    var runner = _executionRunnerFactory(runtimeClient, options.TemplatePublishing);
                    await using (runner.ConfigureAwait(false))
                    {
                        BasicsLiveTrialSnapshotRecord? lastSnapshot = null;
                        try
                        {
                            var finalSnapshot = await runner.RunAsync(
                                    plan,
                                    taskPlugin,
                                    snapshot =>
                                    {
                                        var record = CreateSnapshotRecord(snapshot);
                                        lock (snapshotRecords)
                                        {
                                            snapshotRecords.Add(record);
                                            lastSnapshot = record;
                                        }

                                        onProgress?.Invoke(new BasicsLiveTrialProgress(
                                            trialNumber,
                                            BasicsLiveTrialPhase.Running,
                                            snapshot.StatusText,
                                            record));
                                    },
                                    linkedCts.Token)
                                .ConfigureAwait(false);

                            terminalSnapshot = CreateSnapshotRecord(finalSnapshot);
                            outcome = TranslateOutcome(finalSnapshot.State);
                            outcomeDetail = finalSnapshot.DetailText;
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            terminalSnapshot = lastSnapshot;
                            outcome = BasicsLiveTrialOutcome.TimedOut;
                            outcomeDetail = "trial_timeout";
                        }
                    }

                    var meetsStability = terminalSnapshot is not null
                                         && MeetsStabilityCriteria(terminalSnapshot, options.StabilityCriteria)
                                         && outcome is BasicsLiveTrialOutcome.Succeeded or BasicsLiveTrialOutcome.Stopped;
                    successfulTrialStreak = meetsStability ? successfulTrialStreak + 1 : 0;

                    if (successfulTrialStreak < options.StabilityCriteria.RequiredSuccessfulTrials)
                    {
                        tuningDecision = TryBuildTuningDecision(
                            currentEnvironment,
                            plan,
                            terminalSnapshot,
                            outcome,
                            options.AutoTuning);
                        if (tuningDecision?.Applied == true && tuningDecision.NextConfiguration is not null)
                        {
                            currentEnvironment = ApplyConfigurationSnapshot(currentEnvironment, tuningDecision.NextConfiguration);
                            onProgress?.Invoke(new BasicsLiveTrialProgress(
                                trialNumber,
                                BasicsLiveTrialPhase.Tuning,
                                tuningDecision.Reason,
                                terminalSnapshot,
                                tuningDecision));
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                outcome = BasicsLiveTrialOutcome.TimedOut;
                outcomeDetail = "trial_timeout";
                successfulTrialStreak = 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                outcome = plan is null
                    ? BasicsLiveTrialOutcome.PlanningFailed
                    : BasicsLiveTrialOutcome.Failed;
                outcomeDetail = ex.GetBaseException().Message;
                successfulTrialStreak = 0;
            }
            catch (Exception ex)
            {
                outcome = plan is null
                    ? BasicsLiveTrialOutcome.RuntimeClientFailed
                    : BasicsLiveTrialOutcome.Failed;
                outcomeDetail = ex.GetBaseException().Message;
                successfulTrialStreak = 0;
            }

            stopwatch.Stop();

            var meetsCriteria = terminalSnapshot is not null
                                && MeetsStabilityCriteria(terminalSnapshot, options.StabilityCriteria)
                                && outcome is BasicsLiveTrialOutcome.Succeeded or BasicsLiveTrialOutcome.Stopped;
            var trialResult = new BasicsLiveTrialResult(
                TrialNumber: trialNumber,
                Outcome: outcome,
                OutcomeDetail: outcomeDetail,
                StartedAtUtc: trialStartedAtUtc,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                DurationSeconds: stopwatch.Elapsed.TotalSeconds,
                AppliedConfiguration: trialConfiguration,
                PlanSummary: plan is null ? null : CreatePlanSummary(plan),
                TerminalSnapshot: terminalSnapshot,
                SnapshotCount: snapshotRecords.Count,
                Snapshots: snapshotRecords.ToArray(),
                MeetsStabilityCriteria: meetsCriteria,
                SuccessfulTrialStreakAfterCompletion: successfulTrialStreak,
                TuningDecision: tuningDecision);
            results.Add(trialResult);

            onProgress?.Invoke(new BasicsLiveTrialProgress(
                trialNumber,
                BasicsLiveTrialPhase.Completed,
                $"Trial {trialNumber} completed with outcome {outcome}.",
                terminalSnapshot,
                tuningDecision));

            if (successfulTrialStreak >= options.StabilityCriteria.RequiredSuccessfulTrials)
            {
                break;
            }
        }

        var report = new BasicsLiveTrialReport(
            RunLabel: options.RunLabel,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            StabilityTargetMet: successfulTrialStreak >= options.StabilityCriteria.RequiredSuccessfulTrials,
            ExecutedTrialCount: results.Count,
            StabilityCriteria: options.StabilityCriteria,
            InitialConfiguration: initialConfiguration,
            FinalConfiguration: CreateConfigurationSnapshot(currentEnvironment),
            Trials: results.ToArray());

        onProgress?.Invoke(new BasicsLiveTrialProgress(
            report.ExecutedTrialCount,
            BasicsLiveTrialPhase.Finished,
            report.StabilityTargetMet
                ? $"Stability target met after {report.ExecutedTrialCount} trial(s)."
                : $"Stability target not met after {report.ExecutedTrialCount} trial(s)."));

        return report;
    }

    private static BasicsLiveTrialOutcome TranslateOutcome(BasicsExecutionState state)
    {
        return state switch
        {
            BasicsExecutionState.Succeeded => BasicsLiveTrialOutcome.Succeeded,
            BasicsExecutionState.Stopped or BasicsExecutionState.Stopping => BasicsLiveTrialOutcome.Stopped,
            BasicsExecutionState.Failed => BasicsLiveTrialOutcome.Failed,
            _ => BasicsLiveTrialOutcome.Failed
        };
    }

    private static bool MeetsStabilityCriteria(
        BasicsLiveTrialSnapshotRecord snapshot,
        BasicsLiveTrialStabilityCriteria criteria)
        => snapshot.BestAccuracy >= criteria.TargetAccuracy
           && snapshot.BestFitness >= criteria.TargetFitness;

    private static BasicsLiveTrialPlanSummary CreatePlanSummary(BasicsEnvironmentPlan plan)
        => new(
            plan.SelectedTask.TaskId,
            plan.SelectedTask.DisplayName,
            plan.SeedTemplate.TemplateId,
            plan.OutputObservationMode,
            plan.Capacity.RecommendedInitialPopulationCount,
            plan.Capacity.RecommendedReproductionRunCount,
            plan.Capacity.RecommendedMaxConcurrentBrains,
            plan.Capacity.EligibleWorkerCount,
            plan.Capacity.CapacityScore,
            plan.Capacity.Summary,
            plan.PlannedAtUtc);

    private static BasicsLiveTrialConfigurationSnapshot CreateConfigurationSnapshot(BasicsEnvironmentOptions options)
        => new(
            ClientName: options.ClientName,
            TaskId: options.SelectedTask.TaskId,
            TaskDisplayName: options.SelectedTask.DisplayName,
            TemplateId: options.SeedTemplate.TemplateId,
            TemplateDescription: options.SeedTemplate.Description,
            OutputObservationMode: options.OutputObservationMode,
            VariationBand: new BasicsLiveTrialVariationBandSnapshot(
                options.SeedTemplate.InitialVariationBand.MaxInternalNeuronDelta,
                options.SeedTemplate.InitialVariationBand.MaxAxonDelta,
                options.SeedTemplate.InitialVariationBand.MaxStrengthCodeDelta,
                options.SeedTemplate.InitialVariationBand.MaxParameterCodeDelta,
                options.SeedTemplate.InitialVariationBand.AllowFunctionMutation,
                options.SeedTemplate.InitialVariationBand.AllowAxonReroute,
                options.SeedTemplate.InitialVariationBand.AllowRegionSetChange),
            SeedShape: new BasicsLiveTrialSeedShapeSnapshot(
                options.SeedTemplate.InitialSeedShapeConstraints.MinActiveInternalRegionCount,
                options.SeedTemplate.InitialSeedShapeConstraints.MaxActiveInternalRegionCount,
                options.SeedTemplate.InitialSeedShapeConstraints.MinInternalNeuronCount,
                options.SeedTemplate.InitialSeedShapeConstraints.MaxInternalNeuronCount,
                options.SeedTemplate.InitialSeedShapeConstraints.MinAxonCount,
                options.SeedTemplate.InitialSeedShapeConstraints.MaxAxonCount),
            Sizing: new BasicsLiveTrialSizingSnapshot(
                options.SizingOverrides.InitialPopulationCount,
                options.SizingOverrides.ReproductionRunCount,
                options.SizingOverrides.MaxConcurrentBrains),
            Scheduling: new BasicsLiveTrialSchedulingSnapshot(
                options.Scheduling.ParentSelection.FitnessWeight,
                options.Scheduling.ParentSelection.DiversityWeight,
                options.Scheduling.ParentSelection.SpeciesBalanceWeight,
                options.Scheduling.ParentSelection.EliteFraction,
                options.Scheduling.ParentSelection.ExplorationFraction,
                options.Scheduling.ParentSelection.MaxParentsPerSpecies,
                options.Scheduling.RunAllocation.MinRunsPerPair,
                options.Scheduling.RunAllocation.MaxRunsPerPair,
                options.Scheduling.RunAllocation.FitnessExponent,
                options.Scheduling.RunAllocation.DiversityBoost));

    private static BasicsEnvironmentOptions ApplyConfigurationSnapshot(
        BasicsEnvironmentOptions current,
        BasicsLiveTrialConfigurationSnapshot snapshot)
    {
        var parentSelection = current.Scheduling.ParentSelection with
        {
            FitnessWeight = snapshot.Scheduling.FitnessWeight,
            DiversityWeight = snapshot.Scheduling.DiversityWeight,
            SpeciesBalanceWeight = snapshot.Scheduling.SpeciesBalanceWeight,
            EliteFraction = snapshot.Scheduling.EliteFraction,
            ExplorationFraction = snapshot.Scheduling.ExplorationFraction,
            MaxParentsPerSpecies = snapshot.Scheduling.MaxParentsPerSpecies
        };

        var runAllocation = current.Scheduling.RunAllocation with
        {
            MinRunsPerPair = snapshot.Scheduling.MinRunsPerPair,
            MaxRunsPerPair = snapshot.Scheduling.MaxRunsPerPair,
            FitnessExponent = snapshot.Scheduling.FitnessExponent,
            DiversityBoost = snapshot.Scheduling.DiversityBoost
        };

        return current with
        {
            ClientName = snapshot.ClientName,
            OutputObservationMode = snapshot.OutputObservationMode,
            SeedTemplate = current.SeedTemplate with
            {
                TemplateId = snapshot.TemplateId,
                Description = snapshot.TemplateDescription,
                InitialVariationBand = current.SeedTemplate.InitialVariationBand with
                {
                    MaxInternalNeuronDelta = snapshot.VariationBand.MaxInternalNeuronDelta,
                    MaxAxonDelta = snapshot.VariationBand.MaxAxonDelta,
                    MaxStrengthCodeDelta = snapshot.VariationBand.MaxStrengthCodeDelta,
                    MaxParameterCodeDelta = snapshot.VariationBand.MaxParameterCodeDelta,
                    AllowFunctionMutation = snapshot.VariationBand.AllowFunctionMutation,
                    AllowAxonReroute = snapshot.VariationBand.AllowAxonReroute,
                    AllowRegionSetChange = snapshot.VariationBand.AllowRegionSetChange
                },
                InitialSeedShapeConstraints = current.SeedTemplate.InitialSeedShapeConstraints with
                {
                    MinActiveInternalRegionCount = snapshot.SeedShape.MinActiveInternalRegionCount,
                    MaxActiveInternalRegionCount = snapshot.SeedShape.MaxActiveInternalRegionCount,
                    MinInternalNeuronCount = snapshot.SeedShape.MinInternalNeuronCount,
                    MaxInternalNeuronCount = snapshot.SeedShape.MaxInternalNeuronCount,
                    MinAxonCount = snapshot.SeedShape.MinAxonCount,
                    MaxAxonCount = snapshot.SeedShape.MaxAxonCount
                }
            },
            SizingOverrides = current.SizingOverrides with
            {
                InitialPopulationCount = snapshot.Sizing.InitialPopulationCount,
                ReproductionRunCount = snapshot.Sizing.ReproductionRunCount,
                MaxConcurrentBrains = snapshot.Sizing.MaxConcurrentBrains
            },
            Scheduling = current.Scheduling with
            {
                ParentSelection = parentSelection,
                RunAllocation = runAllocation
            }
        };
    }

    private static BasicsLiveTrialSnapshotRecord CreateSnapshotRecord(BasicsExecutionSnapshot snapshot)
        => new(
            ObservedAtUtc: DateTimeOffset.UtcNow,
            State: snapshot.State,
            StatusText: snapshot.StatusText,
            DetailText: snapshot.DetailText,
            SpeciationEpochId: snapshot.SpeciationEpochId,
            EvaluationFailureCount: snapshot.EvaluationFailureCount,
            EvaluationFailureSummary: snapshot.EvaluationFailureSummary,
            Generation: snapshot.Generation,
            PopulationCount: snapshot.PopulationCount,
            ActiveBrainCount: snapshot.ActiveBrainCount,
            SpeciesCount: snapshot.SpeciesCount,
            ReproductionCalls: snapshot.ReproductionCalls,
            ReproductionRunsObserved: snapshot.ReproductionRunsObserved,
            CapacityUtilization: snapshot.CapacityUtilization,
            BestAccuracy: snapshot.BestAccuracy,
            BestFitness: snapshot.BestFitness,
            MeanFitness: snapshot.MeanFitness,
            BestCandidate: snapshot.BestCandidate is null
                ? null
                : new BasicsLiveTrialBestCandidateRecord(
                    snapshot.BestCandidate.ArtifactSha256,
                    snapshot.BestCandidate.SpeciesId,
                    snapshot.BestCandidate.Accuracy,
                    snapshot.BestCandidate.Fitness,
                    new Dictionary<string, float>(snapshot.BestCandidate.ScoreBreakdown, StringComparer.Ordinal),
                    snapshot.BestCandidate.Diagnostics.ToArray()),
            AccuracyHistory: snapshot.AccuracyHistory.ToArray(),
            BestFitnessHistory: snapshot.BestFitnessHistory.ToArray());

    private static BasicsLiveTuningDecision? TryBuildTuningDecision(
        BasicsEnvironmentOptions current,
        BasicsEnvironmentPlan? plan,
        BasicsLiveTrialSnapshotRecord? terminalSnapshot,
        BasicsLiveTrialOutcome outcome,
        BasicsLiveAutoTuningOptions tuning)
    {
        if (!tuning.Enabled || terminalSnapshot is null)
        {
            return null;
        }

        var next = current;
        var changes = new List<string>();
        string reason;

        if (outcome == BasicsLiveTrialOutcome.TimedOut || terminalSnapshot.EvaluationFailureCount > 0)
        {
            reason = "Observed live-runtime failures or timeouts; reducing pressure and preferring dense vector output.";
            if (tuning.PreferVectorPotentialOnFailures && current.OutputObservationMode == BasicsOutputObservationMode.EventedOutput)
            {
                next = next with { OutputObservationMode = BasicsOutputObservationMode.VectorPotential };
                changes.Add("output_mode=continuous_potential");
            }

            if (tuning.ReduceSizingOnFailures)
            {
                var basisPopulation = current.SizingOverrides.InitialPopulationCount
                                      ?? plan?.Capacity.RecommendedInitialPopulationCount
                                      ?? 64;
                var basisConcurrent = current.SizingOverrides.MaxConcurrentBrains
                                      ?? plan?.Capacity.RecommendedMaxConcurrentBrains
                                      ?? 32;
                var nextPopulation = basisPopulation > 16
                    ? Math.Max(16, (int)Math.Floor(basisPopulation * 0.75d))
                    : basisPopulation;
                var nextConcurrent = basisConcurrent > 1
                    ? Math.Max(1, (int)Math.Floor(basisConcurrent * 0.75d))
                    : basisConcurrent;

                if (current.SizingOverrides.InitialPopulationCount != nextPopulation)
                {
                    next = next with
                    {
                        SizingOverrides = next.SizingOverrides with
                        {
                            InitialPopulationCount = nextPopulation
                        }
                    };
                    changes.Add($"initial_population={nextPopulation}");
                }

                if (current.SizingOverrides.MaxConcurrentBrains != nextConcurrent)
                {
                    next = next with
                    {
                        SizingOverrides = next.SizingOverrides with
                        {
                            MaxConcurrentBrains = nextConcurrent
                        }
                    };
                    changes.Add($"max_concurrent={nextConcurrent}");
                }
            }
        }
        else if (terminalSnapshot.SpeciesCount <= 1 && terminalSnapshot.BestFitness < 0.99f)
        {
            reason = "Species collapsed before reaching target; increasing diversity pressure for the next trial.";
            next = next with
            {
                Scheduling = next.Scheduling with
                {
                    ParentSelection = NormalizeParentSelection(next.Scheduling.ParentSelection with
                    {
                        DiversityWeight = Math.Min(0.60d, next.Scheduling.ParentSelection.DiversityWeight + 0.05d),
                        ExplorationFraction = Math.Min(0.40d, next.Scheduling.ParentSelection.ExplorationFraction + 0.05d)
                    }),
                    RunAllocation = next.Scheduling.RunAllocation with
                    {
                        DiversityBoost = Math.Min(0.75d, next.Scheduling.RunAllocation.DiversityBoost + 0.05d)
                    }
                }
            };
            changes.Add($"diversity_weight={next.Scheduling.ParentSelection.DiversityWeight:0.##}");
            changes.Add($"exploration_fraction={next.Scheduling.ParentSelection.ExplorationFraction:0.##}");
            changes.Add($"diversity_boost={next.Scheduling.RunAllocation.DiversityBoost:0.##}");
        }
        else if (terminalSnapshot.BestFitness < 0.75f)
        {
            reason = "Fitness is still weak; shifting selection toward broader exploration without exceeding runtime bounds.";
            next = next with
            {
                Scheduling = next.Scheduling with
                {
                    ParentSelection = NormalizeParentSelection(next.Scheduling.ParentSelection with
                    {
                        ExplorationFraction = Math.Min(0.40d, next.Scheduling.ParentSelection.ExplorationFraction + 0.05d),
                        DiversityWeight = Math.Min(0.60d, next.Scheduling.ParentSelection.DiversityWeight + 0.03d)
                    }),
                    RunAllocation = next.Scheduling.RunAllocation with
                    {
                        DiversityBoost = Math.Min(0.75d, next.Scheduling.RunAllocation.DiversityBoost + 0.03d)
                    }
                }
            };
            changes.Add($"exploration_fraction={next.Scheduling.ParentSelection.ExplorationFraction:0.##}");
            changes.Add($"diversity_weight={next.Scheduling.ParentSelection.DiversityWeight:0.##}");
            changes.Add($"diversity_boost={next.Scheduling.RunAllocation.DiversityBoost:0.##}");
        }
        else if (terminalSnapshot.BestAccuracy >= 0.85f || terminalSnapshot.BestFitness >= 0.90f)
        {
            reason = "The run is close to target; biasing slightly toward exploitation for the next trial.";
            next = next with
            {
                Scheduling = next.Scheduling with
                {
                    ParentSelection = NormalizeParentSelection(next.Scheduling.ParentSelection with
                    {
                        EliteFraction = Math.Min(0.20d, next.Scheduling.ParentSelection.EliteFraction + 0.05d),
                        ExplorationFraction = Math.Max(0.05d, next.Scheduling.ParentSelection.ExplorationFraction - 0.05d)
                    })
                }
            };
            changes.Add($"elite_fraction={next.Scheduling.ParentSelection.EliteFraction:0.##}");
            changes.Add($"exploration_fraction={next.Scheduling.ParentSelection.ExplorationFraction:0.##}");
        }
        else
        {
            return null;
        }

        if (changes.Count == 0)
        {
            return null;
        }

        return new BasicsLiveTuningDecision(
            Applied: true,
            Reason: reason,
            Changes: changes.ToArray(),
            NextConfiguration: CreateConfigurationSnapshot(next));
    }

    private static BasicsParentSelectionPolicy NormalizeParentSelection(BasicsParentSelectionPolicy policy)
    {
        var elite = Math.Clamp(policy.EliteFraction, 0d, 1d);
        var exploration = Math.Clamp(policy.ExplorationFraction, 0d, 1d);
        if (elite + exploration > 1d)
        {
            exploration = Math.Max(0d, 1d - elite);
        }

        return policy with
        {
            EliteFraction = elite,
            ExplorationFraction = exploration
        };
    }
}
