using System.ComponentModel;
using System.Reflection;
using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;
using Nbn.Proto;
using Nbn.Shared;

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
        IReadOnlyList<float>? behaviorOccupancyHistory = null,
        IReadOnlyList<float>? behaviorPressureHistory = null,
        IReadOnlyList<float>? balancedAccuracyHistory = null,
        IReadOnlyList<float>? bestBalancedAccuracyHistory = null,
        IReadOnlyList<float>? edgeAccuracyHistory = null,
        IReadOnlyList<float>? interiorAccuracyHistory = null,
        BasicsExecutionBestCandidateSummary? bestCandidate = null)
        => new(
            State: BasicsExecutionState.Running,
            StatusText: "Generation 3 evaluated.",
            DetailText: "snapshot for chart test",
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
