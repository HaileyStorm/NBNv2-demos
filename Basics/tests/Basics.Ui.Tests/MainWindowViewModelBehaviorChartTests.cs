using System.ComponentModel;
using System.Reflection;
using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;
using Nbn.Proto;
using Nbn.Shared;
using ProtoPpo = Nbn.Proto.Ppo;

namespace Nbn.Demos.Basics.Ui.Tests;

public sealed class MainWindowViewModelBehaviorChartTests
{
    [Fact]
    public void ApplyExecutionSnapshot_RoutesBehaviorHistoriesToFitnessChartSeries()
    {
        var viewModel = CreateViewModel();
        var changed = ObservePropertyChanges(viewModel);
        var snapshot = CreateSnapshot(
            behaviorOccupancyHistory: new[] { 0.12f, 0.34f, 0.56f },
            behaviorPressureHistory: new[] { 0.01f, 0.08f, 0.21f });

        ApplySnapshot(viewModel, snapshot);

        Assert.Equal(new[] { 0.12f, 0.34f, 0.56f }, viewModel.FitnessTertiaryChartValues);
        Assert.Equal(new[] { 0.01f, 0.08f, 0.21f }, viewModel.FitnessQuaternaryChartValues);
        Assert.True(viewModel.HasFitnessChartData);
        Assert.False(viewModel.ShowFitnessEmptyState);
        Assert.Equal("3", viewModel.FitnessEndGenerationTickLabel);
        Assert.Equal(3, viewModel.FitnessGenerationTicks.Count);
        Assert.Contains(nameof(MainWindowViewModel.FitnessTertiaryChartValues), changed);
        Assert.Contains(nameof(MainWindowViewModel.FitnessQuaternaryChartValues), changed);
        Assert.Contains(nameof(MainWindowViewModel.HasFitnessChartData), changed);
        Assert.Contains(nameof(MainWindowViewModel.FitnessGenerationTicks), changed);
    }

    [Fact]
    public void ApplyExecutionSnapshot_PreservesBehaviorHistories_WhenSnapshotOmitsHistoryArrays()
    {
        var viewModel = CreateViewModel();
        ApplySnapshot(viewModel, CreateSnapshot(
            behaviorOccupancyHistory: new[] { 0.2f, 0.4f },
            behaviorPressureHistory: new[] { 0.1f, 0.3f }));

        ApplySnapshot(viewModel, CreateSnapshot(
            behaviorOccupancyHistory: Array.Empty<float>(),
            behaviorPressureHistory: Array.Empty<float>()));

        Assert.Equal(new[] { 0.2f, 0.4f }, viewModel.FitnessTertiaryChartValues);
        Assert.Equal(new[] { 0.1f, 0.3f }, viewModel.FitnessQuaternaryChartValues);
        Assert.True(viewModel.HasFitnessChartData);
    }

    [Fact]
    public void ApplyExecutionSnapshot_RoutesPartitionedAccuracyHistoriesThroughChartToggles()
    {
        var viewModel = CreateViewModel();
        var snapshot = CreateSnapshot(
            balancedAccuracyHistory: new[] { 0.25f, 0.50f, 0.75f },
            bestBalancedAccuracyHistory: new[] { 0.20f, 0.10f, 0.80f },
            edgeAccuracyHistory: new[] { 0.15f, 0.35f, 0.55f },
            interiorAccuracyHistory: new[] { 0.30f, 0.60f, 0.90f });

        ApplySnapshot(viewModel, snapshot);

        Assert.True(viewModel.ShowAccuracyMetricToggles);
        Assert.True(viewModel.ShowBalancedAccuracyChart);
        Assert.False(viewModel.ShowEdgeAccuracyChart);
        Assert.False(viewModel.ShowInteriorAccuracyChart);
        Assert.Equal(new[] { 0.25f, 0.50f, 0.75f }, viewModel.AccuracyChartValues);
        Assert.Equal(new[] { 0.20f, 0.20f, 0.80f }, viewModel.BestAccuracyChartValues);
        Assert.Empty(viewModel.AccuracyTertiaryChartValues);
        Assert.Empty(viewModel.AccuracyQuaternaryChartValues);

        viewModel.ShowEdgeAccuracyChart = true;
        viewModel.ShowInteriorAccuracyChart = true;

        Assert.Equal(new[] { 0.15f, 0.35f, 0.55f }, viewModel.AccuracyTertiaryChartValues);
        Assert.Equal(new[] { 0.30f, 0.60f, 0.90f }, viewModel.AccuracyQuaternaryChartValues);

        viewModel.ShowBalancedAccuracyChart = false;

        Assert.Empty(viewModel.AccuracyChartValues);
        Assert.Empty(viewModel.BestAccuracyChartValues);
        Assert.Equal(new[] { 0.15f, 0.35f, 0.55f }, viewModel.AccuracyTertiaryChartValues);
        Assert.Equal(new[] { 0.30f, 0.60f, 0.90f }, viewModel.AccuracyQuaternaryChartValues);
    }

    [Fact]
    public void ApplyExecutionSnapshot_UsesSelectionReadyMultiplierForBestReadyBalancedCard()
    {
        var viewModel = CreateViewModel();
        var snapshot = CreateSnapshot(
            bestCandidate: CreateBestCandidate(
                balancedAccuracy: 0.80f,
                edgeAccuracy: 0.70f,
                interiorAccuracy: 0.60f,
                readyConfidence: 0.50f));

        ApplySnapshot(viewModel, snapshot);

        var readyBalanced = Assert.Single(
            viewModel.BestBrainSummaries,
            static metric => metric.MetricId == BasicsMetricId.BestCandidateBalancedAccuracy);
        Assert.Equal("0.42", readyBalanced.ValueText);
    }

    [Fact]
    public void BehaviorTaskSettings_AreSerializedIntoEnvironmentOptions()
    {
        var viewModel = CreateViewModel();
        viewModel.MultiplicationBehaviorOccupancyEnabled = false;
        viewModel.MultiplicationBehaviorRampStartText = "0.25";
        viewModel.MultiplicationBehaviorRampFullText = "0.75";

        var options = BuildEnvironmentOptions(viewModel);

        Assert.False(options.TaskSettings.Multiplication.BehaviorOccupancyEnabled);
        Assert.Equal(0.25f, options.TaskSettings.Multiplication.BehaviorStageGateStart);
        Assert.Equal(0.75f, options.TaskSettings.Multiplication.BehaviorStageGateFull);
    }

    [Fact]
    public void PpoOptimizerSettings_AreSerializedIntoEnvironmentOptions()
    {
        var viewModel = CreateViewModel();
        viewModel.PpoOptimizerEnabled = true;
        viewModel.PpoObjectiveName = "multiplication";
        viewModel.PpoRewardSignal = "basics.fitness";
        viewModel.PpoRolloutTickCountText = "256";
        viewModel.PpoRolloutBatchCountText = "8";
        viewModel.PpoClipEpsilonText = "0.15";
        viewModel.PpoDiscountGammaText = "0.98";
        viewModel.PpoGaeLambdaText = "0.90";
        viewModel.PpoLearningRateText = "0.0002";
        viewModel.PpoOptimizationEpochCountText = "5";
        viewModel.PpoMinibatchSizeText = "16";
        viewModel.PpoSeedText = "1234";

        var options = BuildEnvironmentOptions(viewModel);

        Assert.True(options.PpoOptimizer.Enabled);
        Assert.False(options.PpoOptimizer.DirectRuntimeControlEnabled);
        Assert.Equal("multiplication", options.PpoOptimizer.ObjectiveName);
        Assert.Equal("basics.fitness", options.PpoOptimizer.RewardSignal);
        Assert.Equal((ulong)256, options.PpoOptimizer.RolloutTickCount);
        Assert.Equal((ulong)8, options.PpoOptimizer.RolloutBatchCount);
        Assert.Equal(0.15f, options.PpoOptimizer.ClipEpsilon);
        Assert.Equal(0.98f, options.PpoOptimizer.DiscountGamma);
        Assert.Equal(0.90f, options.PpoOptimizer.GaeLambda);
        Assert.Equal(0.0002f, options.PpoOptimizer.LearningRate);
        Assert.Equal((uint)5, options.PpoOptimizer.OptimizationEpochCount);
        Assert.Equal((uint)16, options.PpoOptimizer.MinibatchSize);
        Assert.Equal((ulong)1234, options.PpoOptimizer.Seed);
    }

    [Fact]
    public void DirectRuntimeControlSettings_AreSerializedWithoutPpoManagerMode()
    {
        var viewModel = CreateViewModel();
        viewModel.PpoOptimizerEnabled = false;
        viewModel.DirectRuntimeControlEnabled = true;
        viewModel.PpoObjectiveName = "multiplication";
        viewModel.PpoRewardSignal = "basics.direct_sample_reward";
        viewModel.PpoRolloutTickCountText = "not-a-number";
        viewModel.PpoClipEpsilonText = "not-a-number";

        var options = BuildEnvironmentOptions(viewModel);

        Assert.False(options.PpoOptimizer.Enabled);
        Assert.True(options.PpoOptimizer.DirectRuntimeControlEnabled);
        Assert.Equal("multiplication", options.PpoOptimizer.ObjectiveName);
        Assert.Equal("basics.direct_sample_reward", options.PpoOptimizer.RewardSignal);
        Assert.Equal((ulong)12, options.PpoOptimizer.RolloutTickCount);
        Assert.Equal(0.2f, options.PpoOptimizer.ClipEpsilon);
        Assert.False(viewModel.ShowPpoOptimizerConfiguration);
        Assert.False(viewModel.ShowPpoServiceStatus);
        Assert.True(viewModel.ShowLocalReproductionSchedulingControls);
        Assert.Contains("Direct brain reward-control", viewModel.PpoOptimizerDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledPpoOptimizer_IgnoresHiddenHyperparameterText()
    {
        var viewModel = CreateViewModel();
        viewModel.PpoOptimizerEnabled = false;
        viewModel.PpoRolloutTickCountText = "not-a-number";
        viewModel.PpoClipEpsilonText = "not-a-number";

        var options = BuildEnvironmentOptions(viewModel);

        Assert.False(options.PpoOptimizer.Enabled);
        Assert.False(options.PpoOptimizer.DirectRuntimeControlEnabled);
        Assert.Equal((ulong)12, options.PpoOptimizer.RolloutTickCount);
        Assert.Equal(0.2f, options.PpoOptimizer.ClipEpsilon);
    }

    [Fact]
    public void MultiplicationSelection_ShowsPpoSettings_WithoutAutoEnablingService()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedTask = viewModel.Tasks.First(task => task.TaskId == "multiplication");

        Assert.True(viewModel.ShowPpoOptimizerSettings);
        Assert.False(viewModel.PpoOptimizerEnabled);
        Assert.False(viewModel.ShowPpoOptimizerConfiguration);
        Assert.True(viewModel.ShowLocalReproductionSchedulingControls);
        Assert.False(viewModel.ShowPpoSchedulingNotice);
        Assert.Equal("Optimization Mode: Local Reproduction", viewModel.OptimizationModeTitle);

        viewModel.PpoOptimizerEnabled = true;

        Assert.True(viewModel.ShowPpoOptimizerConfiguration);
        Assert.True(viewModel.ShowPpoServiceStatus);
        Assert.False(viewModel.ShowLocalReproductionSchedulingControls);
        Assert.True(viewModel.ShowPpoSchedulingNotice);
        Assert.Equal("Generation Controller: Runtime PPO Reproduction Policy", viewModel.OptimizationModeTitle);
        Assert.Equal("PPO Parent Context + Runtime Policy", viewModel.SchedulingSectionTitle);
        Assert.Contains("generation control", viewModel.PpoOptimizerDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reward feedback", viewModel.PpoOptimizerDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reproduction actions", viewModel.PpoOptimizerDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multiplication", viewModel.PpoOptimizerDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings Manager", viewModel.PpoOptimizerDetail, StringComparison.Ordinal);
        Assert.Contains("owns candidate selection", viewModel.SchedulingSectionDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connect to IO", viewModel.PpoServiceDetail, StringComparison.Ordinal);

        viewModel.SelectedTask = viewModel.Tasks.First(task => task.TaskId == "and");

        Assert.False(viewModel.PpoOptimizerEnabled);
        Assert.False(viewModel.ShowPpoOptimizerConfiguration);
        Assert.True(viewModel.ShowLocalReproductionSchedulingControls);
    }

    [Fact]
    public void PpoControllingMode_IgnoresHiddenLocalSchedulingText()
    {
        var viewModel = CreateViewModel();
        viewModel.PpoOptimizerEnabled = true;
        viewModel.FitnessWeightText = "not-a-number";
        viewModel.MinRunsPerPairText = "not-a-number";
        viewModel.AdaptiveDiversityStallGenerationWindowText = "not-a-number";

        var options = BuildEnvironmentOptions(viewModel);

        Assert.True(options.PpoOptimizer.Enabled);
        Assert.Equal(new BasicsReproductionSchedulingPolicy(), options.Scheduling);
        Assert.Equal(new BasicsAdaptiveDiversityOptions(), options.AdaptiveDiversity);
    }

    [Fact]
    public void BehaviorTaskSettingChanges_RaiseTaskAndChartBindingNotifications()
    {
        var viewModel = CreateViewModel();
        var changed = ObservePropertyChanges(viewModel);

        viewModel.MultiplicationBehaviorOccupancyEnabled = false;

        Assert.Contains(nameof(MainWindowViewModel.TaskSettingsDetail), changed);
        Assert.Contains(nameof(MainWindowViewModel.ShowMultiplicationTaskSettings), changed);
        Assert.Contains(nameof(MainWindowViewModel.FitnessTertiaryChartValues), changed);
        Assert.Contains(nameof(MainWindowViewModel.FitnessQuaternaryChartValues), changed);
    }

    [Fact]
    public void PpoSettingChanges_RaiseVisibilityAndDetailNotifications()
    {
        var viewModel = CreateViewModel();
        var changed = ObservePropertyChanges(viewModel);

        viewModel.PpoOptimizerEnabled = true;

        Assert.Contains(nameof(MainWindowViewModel.ShowPpoOptimizerSettings), changed);
        Assert.Contains(nameof(MainWindowViewModel.ShowPpoOptimizerConfiguration), changed);
        Assert.Contains(nameof(MainWindowViewModel.ShowPpoServiceStatus), changed);
        Assert.Contains(nameof(MainWindowViewModel.ShowLocalReproductionSchedulingControls), changed);
        Assert.Contains(nameof(MainWindowViewModel.ShowPpoSchedulingNotice), changed);
        Assert.Contains(nameof(MainWindowViewModel.OptimizationModeTitle), changed);
        Assert.Contains(nameof(MainWindowViewModel.PpoOptimizerDetail), changed);
        Assert.Contains(nameof(MainWindowViewModel.SchedulingSectionTitle), changed);
        Assert.Contains(nameof(MainWindowViewModel.SchedulingSectionDetail), changed);
    }

    [Fact]
    public void PpoStatusSummary_NamesDiscoveryMismatch_WhenIoCannotSeeManager()
    {
        var summary = BuildPpoServiceStatus(new ProtoPpo.PpoStatusResponse
        {
            FailureReason = ProtoPpo.PpoFailureReason.PpoFailureServiceUnavailable,
            FailureDetail = "PpoStatus failed: PPO manager endpoint is not configured."
        });

        Assert.False(summary.IsReady);
        Assert.Equal("PPO manager not visible to IO.", summary.Status);
        Assert.Contains("service.endpoint.ppo_manager", summary.Detail, StringComparison.Ordinal);
        Assert.Contains("same SettingsMonitor host, port, and actor name", summary.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PpoStatusSummary_ReportsReadyDependencies_WhenManagerIsAvailable()
    {
        var summary = BuildPpoServiceStatus(new ProtoPpo.PpoStatusResponse
        {
            FailureReason = ProtoPpo.PpoFailureReason.PpoFailureNone,
            Dependencies = new ProtoPpo.PpoDependencyStatus
            {
                IoAvailable = true,
                IoEndpoint = "127.0.0.1:12020/io-gateway",
                ReproductionAvailable = true,
                ReproductionEndpoint = "127.0.0.1:12070/ReproductionManager",
                SpeciationAvailable = true,
                SpeciationEndpoint = "127.0.0.1:12080/SpeciationManager"
            }
        });

        Assert.True(summary.IsReady);
        Assert.Equal("PPO core service ready.", summary.Status);
        Assert.Contains("IO 127.0.0.1:12020/io-gateway", summary.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyExecutionSnapshot_ShowsPpoDiscoveryFailureText()
    {
        var viewModel = CreateViewModel();
        var snapshot = CreateSnapshot(
            state: BasicsExecutionState.Failed,
            statusText: "PPO core service unavailable.",
            detailText: "PPO status failed: PpoFailureServiceUnavailable PpoStatus failed: PPO manager endpoint is not configured. PPO may be running, but this IO Gateway has not discovered service.endpoint.ppo_manager in its SettingsMonitor. Verify Workbench, IO, and PPO use the same SettingsMonitor host, port, and actor name, or disable PPO.");

        ApplySnapshot(viewModel, snapshot);

        Assert.Equal("PPO core service unavailable.", viewModel.ExecutionStatus);
        Assert.Contains("service.endpoint.ppo_manager", viewModel.ExecutionDetail, StringComparison.Ordinal);
        Assert.Contains("same SettingsMonitor host, port, and actor name", viewModel.ExecutionDetail, StringComparison.OrdinalIgnoreCase);
    }

    private static MainWindowViewModel CreateViewModel()
        => new(
            new UiDispatcher(),
            new StubArtifactExportService(),
            new StubBrainImportService(),
            new StubWorkerProcessService());

    private static List<string> ObservePropertyChanges(INotifyPropertyChanged source)
    {
        var changed = new List<string>();
        source.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                changed.Add(args.PropertyName);
            }
        };
        return changed;
    }

    private static void ApplySnapshot(MainWindowViewModel viewModel, BasicsExecutionSnapshot snapshot)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ApplyExecutionSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(viewModel, new object[] { snapshot, 32 });
    }

    private static BasicsEnvironmentOptions BuildEnvironmentOptions(MainWindowViewModel viewModel)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "TryBuildEnvironmentOptions",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var arguments = new object?[] { null, null };
        var result = method.Invoke(viewModel, arguments);
        Assert.True((bool)result!);
        return Assert.IsType<BasicsEnvironmentOptions>(arguments[0]);
    }

    private static BasicsExecutionSnapshot CreateSnapshot(
        BasicsExecutionState state = BasicsExecutionState.Running,
        string statusText = "Generation 3 evaluated.",
        string detailText = "snapshot for chart test",
        IReadOnlyList<float>? behaviorOccupancyHistory = null,
        IReadOnlyList<float>? behaviorPressureHistory = null,
        IReadOnlyList<float>? balancedAccuracyHistory = null,
        IReadOnlyList<float>? bestBalancedAccuracyHistory = null,
        IReadOnlyList<float>? edgeAccuracyHistory = null,
        IReadOnlyList<float>? interiorAccuracyHistory = null,
        BasicsExecutionBestCandidateSummary? bestCandidate = null)
        => new(
            State: state,
            StatusText: statusText,
            DetailText: detailText,
            SpeciationEpochId: null,
            EvaluationFailureCount: 0,
            EvaluationFailureSummary: string.Empty,
            Generation: 3,
            PopulationCount: 64,
            ActiveBrainCount: 0,
            SpeciesCount: 1,
            ReproductionCalls: 0,
            ReproductionRunsObserved: 0,
            CapacityUtilization: 0f,
            OffspringBestAccuracy: 0.3f,
            BestAccuracy: 0.4f,
            OffspringBestFitness: 0.5f,
            BestFitness: 0.6f,
            MeanFitness: 0.45f,
            EffectiveTemplateDefinition: null,
            SeedShape: null,
            BestCandidate: bestCandidate,
            OffspringAccuracyHistory: new[] { 0.1f, 0.2f, 0.3f },
            AccuracyHistory: new[] { 0.15f, 0.25f, 0.35f },
            OffspringBalancedAccuracyHistory: balancedAccuracyHistory ?? Array.Empty<float>(),
            BalancedAccuracyHistory: bestBalancedAccuracyHistory ?? Array.Empty<float>(),
            OffspringEdgeAccuracyHistory: edgeAccuracyHistory ?? Array.Empty<float>(),
            OffspringInteriorAccuracyHistory: interiorAccuracyHistory ?? Array.Empty<float>(),
            OffspringFitnessHistory: new[] { 0.2f, 0.4f, 0.5f },
            BestFitnessHistory: new[] { 0.3f, 0.2f, 0.6f })
        {
            BehaviorOccupancyHistory = behaviorOccupancyHistory ?? Array.Empty<float>(),
            BehaviorPressureHistory = behaviorPressureHistory ?? Array.Empty<float>()
        };

    private static PpoServiceStatusSummary BuildPpoServiceStatus(ProtoPpo.PpoStatusResponse? status)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "BuildPpoServiceStatus",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsType<PpoServiceStatusSummary>(method!.Invoke(null, new object?[] { status }));
    }

    private static BasicsExecutionBestCandidateSummary CreateBestCandidate(
        float balancedAccuracy,
        float edgeAccuracy,
        float interiorAccuracy,
        float readyConfidence)
        => new(
            DefinitionArtifact: new string('a', 64).ToArtifactRef(128, "application/x-nbn", "http://fake-store/best"),
            SnapshotArtifact: null,
            ActiveBrainId: null,
            SpeciesId: "species.test",
            Accuracy: 0.75f,
            Fitness: 0.70f,
            Complexity: null,
            ScoreBreakdown: new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["balanced_tolerance_accuracy"] = balancedAccuracy,
                ["edge_tolerance_accuracy"] = edgeAccuracy,
                ["interior_tolerance_accuracy"] = interiorAccuracy,
                ["ready_confidence"] = readyConfidence
            },
            Diagnostics: Array.Empty<string>(),
            Generation: 3);

    private sealed class StubArtifactExportService : IBasicsArtifactExportService
    {
        public Task<string?> ExportAsync(
            ArtifactRef artifact,
            string title,
            string suggestedFileName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubBrainImportService : IBasicsBrainImportService
    {
        public Task<IReadOnlyList<BasicsImportedBrainFile>> ImportAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BasicsImportedBrainFile>>(Array.Empty<BasicsImportedBrainFile>());
    }

    private sealed class StubWorkerProcessService : IBasicsLocalWorkerProcessService
    {
        public int LaunchedWorkerCount => 0;

        public Task<BasicsLocalWorkerLaunchResult> StartWorkersAsync(
            BasicsLocalWorkerLaunchRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BasicsLocalWorkerLaunchResult(
                Success: true,
                StartedCount: 0,
                StartedWorkers: Array.Empty<BasicsLocalWorkerInfo>(),
                StatusText: "not launched",
                DetailText: "test stub"));

        public Task<BasicsLocalWorkerStopResult> StopLaunchedWorkersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BasicsLocalWorkerStopResult(0, "not launched", "test stub"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
