using System.Collections.ObjectModel;
using System.Globalization;
using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Proto;
using Nbn.Shared;
using Repro = Nbn.Proto.Repro;

namespace Nbn.Demos.Basics.Ui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly HashSet<string> NonEditableProperties = new(StringComparer.Ordinal)
    {
        nameof(ConnectionStatus),
        nameof(CapacityStatus),
        nameof(CapacitySummary),
        nameof(ValidationSummary),
        nameof(RecommendedInitialPopulationText),
        nameof(RecommendedRunCountText),
        nameof(RecommendedMaxConcurrentBrainsText),
        nameof(LastPlanSummary),
        nameof(InputWidthDisplay),
        nameof(OutputWidthDisplay),
        nameof(ExpectSingleBootstrapSpeciesDisplay),
        nameof(AllowOffTemplateSeedsDisplay),
        nameof(ProtectIoRegionNeuronCountsDisplay),
        nameof(HasAccuracyChartData),
        nameof(HasFitnessChartData),
        nameof(ShowAccuracyEmptyState),
        nameof(ShowFitnessEmptyState),
        nameof(AccuracyChartPoints),
        nameof(FitnessChartPoints),
        nameof(ExecutionStatus),
        nameof(ExecutionDetail),
        nameof(IsExecutionRunning),
        nameof(MetricsStatus),
        nameof(MetricsSecondaryStatus),
        nameof(WinnerExportStatus),
        nameof(WinnerExportDetail)
    };

    private readonly UiDispatcher _dispatcher;
    private readonly IBasicsArtifactExportService _artifactExportService;
    private IBasicsRuntimeClient? _runtimeClient;
    private BasicsExecutionSession? _executionSession;
    private CancellationTokenSource? _executionCts;
    private BasicsEnvironmentPlan? _lastPlan;
    private bool _suppressValidationRefresh;
    private bool _isExecutionRunning;
    private readonly List<float> _accuracyHistory = new();
    private readonly List<float> _fitnessHistory = new();

    private string _ioAddress = "127.0.0.1:12050";
    private string _ioGatewayName = "io-gateway";
    private string _clientName = "nbn.basics.ui";
    private string _bindHost = NetworkAddressDefaults.DefaultBindHost;
    private string _portText = "12094";
    private string _advertiseHost = string.Empty;
    private string _advertisePortText = string.Empty;
    private string _requestTimeoutSecondsText = "30";
    private string _optionalSettingsAddress = string.Empty;
    private string _optionalSettingsActorName = "SettingsMonitor";
    private string _connectionStatus = "Disconnected";
    private string _capacityStatus = "No capacity snapshot fetched.";
    private string _capacitySummary = "Fetch capacity through IO to compute population bounds.";
    private string _validationSummary = "Configuration not yet validated.";
    private string _templateId = "basics-template-a";
    private string _templateDescription = "Seed all initial brains from one shared 2→1 template, allowing only bounded minor divergence.";
    private string _templateArtifactSha256 = string.Empty;
    private string _templateArtifactMediaType = "application/x-nbn";
    private string _templateArtifactSizeBytesText = string.Empty;
    private string _templateArtifactStoreUri = string.Empty;
    private string _minActiveInternalRegionCountText = string.Empty;
    private string _maxActiveInternalRegionCountText = string.Empty;
    private string _minInternalNeuronCountText = string.Empty;
    private string _maxInternalNeuronCountText = string.Empty;
    private string _minAxonCountText = string.Empty;
    private string _maxAxonCountText = string.Empty;
    private string _maxInternalNeuronDeltaText = "2";
    private string _maxAxonDeltaText = "8";
    private string _maxStrengthCodeDeltaText = "4";
    private string _maxParameterCodeDeltaText = "4";
    private bool _allowFunctionMutation;
    private bool _allowAxonReroute = true;
    private bool _allowRegionSetChange;
    private string _initialPopulationOverrideText = string.Empty;
    private string _reproductionRunCountOverrideText = string.Empty;
    private string _maxConcurrentBrainsOverrideText = string.Empty;
    private string _recommendedInitialPopulationText = "—";
    private string _recommendedRunCountText = "—";
    private string _recommendedMaxConcurrentBrainsText = "—";
    private string _targetAccuracyText = "1.0";
    private string _targetFitnessText = "0.999";
    private string _fitnessWeightText = "0.55";
    private string _diversityWeightText = "0.35";
    private string _speciesBalanceWeightText = "0.15";
    private string _eliteFractionText = "0.10";
    private string _explorationFractionText = "0.25";
    private string _maxParentsPerSpeciesText = "8";
    private string _minRunsPerPairText = "2";
    private string _maxRunsPerPairText = "12";
    private string _fitnessExponentText = "1.20";
    private string _diversityBoostText = "0.35";
    private string _lastPlanSummary = "No environment plan built yet.";
    private string _executionStatus = "Idle";
    private string _executionDetail = "Connect to IO, build capacity bounds, then start an implemented task plugin.";
    private string _metricsStatus = "Connect to IO, fetch capacity, and start an implemented task to populate live metrics.";
    private string _metricsSecondaryStatus = "Population and resource summaries update when capacity is fetched or a run is active.";
    private string _winnerExportStatus = "No winning brain retained.";
    private string _winnerExportDetail = "When a run meets the stop target, the simplest qualifying winner stays active for export.";
    private ArtifactRef? _winnerDefinitionArtifact;
    private ArtifactRef? _winnerSnapshotArtifact;
    private Guid _retainedWinnerBrainId;
    private TaskOption? _selectedTask;
    private StrengthSourceOption? _selectedStrengthSource;
    private OutputObservationModeOption? _selectedOutputObservationMode;

    public MainWindowViewModel(
        UiDispatcher dispatcher,
        IBasicsArtifactExportService artifactExportService)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _artifactExportService = artifactExportService ?? throw new ArgumentNullException(nameof(artifactExportService));

        OutputObservationModes = new ObservableCollection<OutputObservationModeOption>(BuildOutputObservationModes());
        SelectedOutputObservationMode = OutputObservationModes.First(static option => option.Mode == BasicsOutputObservationMode.VectorPotential);

        Tasks = new ObservableCollection<TaskOption>(BuildTasks());

        StrengthSources = new ObservableCollection<StrengthSourceOption>(BuildStrengthSources());
        SelectedStrengthSource = StrengthSources.First(static option => option.Value == Repro.StrengthSource.StrengthBaseOnly);

        ValidationErrors = new ObservableCollection<string>();
        MetricSummaries = new ObservableCollection<MetricSummaryItemViewModel>(BuildMetricSummaryItems());

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
        FetchCapacityCommand = new AsyncRelayCommand(FetchCapacityAsync, CanBuildPlan);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
        ExportDefinitionCommand = new AsyncRelayCommand(ExportWinningDefinitionAsync, CanExportWinnerDefinition);
        ExportSnapshotCommand = new AsyncRelayCommand(ExportWinningSnapshotAsync, CanExportWinnerSnapshot);
        ApplySuggestedBoundsCommand = new RelayCommand(ApplySuggestedBounds, () => _lastPlan is not null);

        SelectedTask = Tasks.FirstOrDefault();

        PropertyChanged += (_, args) =>
        {
            if (_suppressValidationRefresh)
            {
                return;
            }

            if (args.PropertyName is null || NonEditableProperties.Contains(args.PropertyName))
            {
                return;
            }

            RefreshValidationState();
        };

        RefreshValidationState();
    }

    public ObservableCollection<TaskOption> Tasks { get; }

    public ObservableCollection<StrengthSourceOption> StrengthSources { get; }

    public ObservableCollection<OutputObservationModeOption> OutputObservationModes { get; }

    public ObservableCollection<string> ValidationErrors { get; }

    public ObservableCollection<MetricSummaryItemViewModel> MetricSummaries { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand FetchCapacityCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ExportDefinitionCommand { get; }

    public AsyncRelayCommand ExportSnapshotCommand { get; }

    public RelayCommand ApplySuggestedBoundsCommand { get; }

    public string IoAddress
    {
        get => _ioAddress;
        set => SetProperty(ref _ioAddress, value);
    }

    public string IoGatewayName
    {
        get => _ioGatewayName;
        set => SetProperty(ref _ioGatewayName, value);
    }

    public string ClientName
    {
        get => _clientName;
        set => SetProperty(ref _clientName, value);
    }

    public string BindHost
    {
        get => _bindHost;
        set => SetProperty(ref _bindHost, value);
    }

    public string PortText
    {
        get => _portText;
        set => SetProperty(ref _portText, value);
    }

    public string AdvertiseHost
    {
        get => _advertiseHost;
        set => SetProperty(ref _advertiseHost, value);
    }

    public string AdvertisePortText
    {
        get => _advertisePortText;
        set => SetProperty(ref _advertisePortText, value);
    }

    public string RequestTimeoutSecondsText
    {
        get => _requestTimeoutSecondsText;
        set => SetProperty(ref _requestTimeoutSecondsText, value);
    }

    public string OptionalSettingsAddress
    {
        get => _optionalSettingsAddress;
        set => SetProperty(ref _optionalSettingsAddress, value);
    }

    public string OptionalSettingsActorName
    {
        get => _optionalSettingsActorName;
        set => SetProperty(ref _optionalSettingsActorName, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string CapacityStatus
    {
        get => _capacityStatus;
        private set => SetProperty(ref _capacityStatus, value);
    }

    public string CapacitySummary
    {
        get => _capacitySummary;
        private set => SetProperty(ref _capacitySummary, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public TaskOption? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetProperty(ref _selectedTask, value))
            {
                return;
            }

            ApplyTaskDefaults(value);
        }
    }

    public string TemplateId
    {
        get => _templateId;
        set => SetProperty(ref _templateId, value);
    }

    public string TemplateDescription
    {
        get => _templateDescription;
        set => SetProperty(ref _templateDescription, value);
    }

    public string TemplateArtifactSha256
    {
        get => _templateArtifactSha256;
        set => SetProperty(ref _templateArtifactSha256, value);
    }

    public string TemplateArtifactMediaType
    {
        get => _templateArtifactMediaType;
        set => SetProperty(ref _templateArtifactMediaType, value);
    }

    public string TemplateArtifactSizeBytesText
    {
        get => _templateArtifactSizeBytesText;
        set => SetProperty(ref _templateArtifactSizeBytesText, value);
    }

    public string TemplateArtifactStoreUri
    {
        get => _templateArtifactStoreUri;
        set => SetProperty(ref _templateArtifactStoreUri, value);
    }

    public string MinActiveInternalRegionCountText
    {
        get => _minActiveInternalRegionCountText;
        set => SetProperty(ref _minActiveInternalRegionCountText, value);
    }

    public string MaxActiveInternalRegionCountText
    {
        get => _maxActiveInternalRegionCountText;
        set => SetProperty(ref _maxActiveInternalRegionCountText, value);
    }

    public string MinInternalNeuronCountText
    {
        get => _minInternalNeuronCountText;
        set => SetProperty(ref _minInternalNeuronCountText, value);
    }

    public string MaxInternalNeuronCountText
    {
        get => _maxInternalNeuronCountText;
        set => SetProperty(ref _maxInternalNeuronCountText, value);
    }

    public string MinAxonCountText
    {
        get => _minAxonCountText;
        set => SetProperty(ref _minAxonCountText, value);
    }

    public string MaxAxonCountText
    {
        get => _maxAxonCountText;
        set => SetProperty(ref _maxAxonCountText, value);
    }

    public string MinActiveInternalRegionCountWatermark => "1";

    public string MaxActiveInternalRegionCountWatermark => "1";

    public string MinInternalNeuronCountWatermark => "1";

    public string MaxInternalNeuronCountWatermark => "1";

    public string MinAxonCountWatermark => "3";

    public string MaxAxonCountWatermark => "3";

    public string InputWidthDisplay => BasicsIoGeometry.InputWidth.ToString(CultureInfo.InvariantCulture);

    public string OutputWidthDisplay => BasicsIoGeometry.OutputWidth.ToString(CultureInfo.InvariantCulture);

    public string ExpectSingleBootstrapSpeciesDisplay => "true";

    public string AllowOffTemplateSeedsDisplay => "false";

    public string MaxInternalNeuronDeltaText
    {
        get => _maxInternalNeuronDeltaText;
        set => SetProperty(ref _maxInternalNeuronDeltaText, value);
    }

    public string MaxAxonDeltaText
    {
        get => _maxAxonDeltaText;
        set => SetProperty(ref _maxAxonDeltaText, value);
    }

    public string MaxStrengthCodeDeltaText
    {
        get => _maxStrengthCodeDeltaText;
        set => SetProperty(ref _maxStrengthCodeDeltaText, value);
    }

    public string MaxParameterCodeDeltaText
    {
        get => _maxParameterCodeDeltaText;
        set => SetProperty(ref _maxParameterCodeDeltaText, value);
    }

    public bool AllowFunctionMutation
    {
        get => _allowFunctionMutation;
        set => SetProperty(ref _allowFunctionMutation, value);
    }

    public bool AllowAxonReroute
    {
        get => _allowAxonReroute;
        set => SetProperty(ref _allowAxonReroute, value);
    }

    public bool AllowRegionSetChange
    {
        get => _allowRegionSetChange;
        set => SetProperty(ref _allowRegionSetChange, value);
    }

    public string InitialPopulationOverrideText
    {
        get => _initialPopulationOverrideText;
        set => SetProperty(ref _initialPopulationOverrideText, value);
    }

    public string ReproductionRunCountOverrideText
    {
        get => _reproductionRunCountOverrideText;
        set => SetProperty(ref _reproductionRunCountOverrideText, value);
    }

    public string MaxConcurrentBrainsOverrideText
    {
        get => _maxConcurrentBrainsOverrideText;
        set => SetProperty(ref _maxConcurrentBrainsOverrideText, value);
    }

    public string RecommendedInitialPopulationText
    {
        get => _recommendedInitialPopulationText;
        private set => SetProperty(ref _recommendedInitialPopulationText, value);
    }

    public string RecommendedRunCountText
    {
        get => _recommendedRunCountText;
        private set => SetProperty(ref _recommendedRunCountText, value);
    }

    public string RecommendedMaxConcurrentBrainsText
    {
        get => _recommendedMaxConcurrentBrainsText;
        private set => SetProperty(ref _recommendedMaxConcurrentBrainsText, value);
    }

    public string TargetAccuracyText
    {
        get => _targetAccuracyText;
        set => SetProperty(ref _targetAccuracyText, value);
    }

    public string TargetFitnessText
    {
        get => _targetFitnessText;
        set => SetProperty(ref _targetFitnessText, value);
    }

    public StrengthSourceOption? SelectedStrengthSource
    {
        get => _selectedStrengthSource;
        set => SetProperty(ref _selectedStrengthSource, value);
    }

    public OutputObservationModeOption? SelectedOutputObservationMode
    {
        get => _selectedOutputObservationMode;
        set => SetProperty(ref _selectedOutputObservationMode, value);
    }

    public string ProtectIoRegionNeuronCountsDisplay => "true";

    public string FitnessWeightText
    {
        get => _fitnessWeightText;
        set => SetProperty(ref _fitnessWeightText, value);
    }

    public string DiversityWeightText
    {
        get => _diversityWeightText;
        set => SetProperty(ref _diversityWeightText, value);
    }

    public string SpeciesBalanceWeightText
    {
        get => _speciesBalanceWeightText;
        set => SetProperty(ref _speciesBalanceWeightText, value);
    }

    public string EliteFractionText
    {
        get => _eliteFractionText;
        set => SetProperty(ref _eliteFractionText, value);
    }

    public string ExplorationFractionText
    {
        get => _explorationFractionText;
        set => SetProperty(ref _explorationFractionText, value);
    }

    public string MaxParentsPerSpeciesText
    {
        get => _maxParentsPerSpeciesText;
        set => SetProperty(ref _maxParentsPerSpeciesText, value);
    }

    public string MinRunsPerPairText
    {
        get => _minRunsPerPairText;
        set => SetProperty(ref _minRunsPerPairText, value);
    }

    public string MaxRunsPerPairText
    {
        get => _maxRunsPerPairText;
        set => SetProperty(ref _maxRunsPerPairText, value);
    }

    public string FitnessExponentText
    {
        get => _fitnessExponentText;
        set => SetProperty(ref _fitnessExponentText, value);
    }

    public string DiversityBoostText
    {
        get => _diversityBoostText;
        set => SetProperty(ref _diversityBoostText, value);
    }

    public string LastPlanSummary
    {
        get => _lastPlanSummary;
        private set => SetProperty(ref _lastPlanSummary, value);
    }

    public string ExecutionStatus
    {
        get => _executionStatus;
        private set => SetProperty(ref _executionStatus, value);
    }

    public string ExecutionDetail
    {
        get => _executionDetail;
        private set => SetProperty(ref _executionDetail, value);
    }

    public bool IsExecutionRunning
    {
        get => _isExecutionRunning;
        private set => SetProperty(ref _isExecutionRunning, value);
    }

    public string MetricsStatus
    {
        get => _metricsStatus;
        private set => SetProperty(ref _metricsStatus, value);
    }

    public string MetricsSecondaryStatus
    {
        get => _metricsSecondaryStatus;
        private set => SetProperty(ref _metricsSecondaryStatus, value);
    }

    public string WinnerExportStatus
    {
        get => _winnerExportStatus;
        private set => SetProperty(ref _winnerExportStatus, value);
    }

    public string WinnerExportDetail
    {
        get => _winnerExportDetail;
        private set => SetProperty(ref _winnerExportDetail, value);
    }

    public bool HasAccuracyChartData => _accuracyHistory.Count > 0;

    public bool HasFitnessChartData => _fitnessHistory.Count > 0;

    public bool ShowAccuracyEmptyState => !HasAccuracyChartData;

    public bool ShowFitnessEmptyState => !HasFitnessChartData;

    public string AccuracyChartPoints => BuildChartPoints(_accuracyHistory);

    public string FitnessChartPoints => BuildChartPoints(_fitnessHistory);

    private async Task ConnectAsync()
    {
        if (!TryBuildRuntimeClientOptions(out var options, out var errors))
        {
            ApplyValidationMessages(errors);
            ConnectionStatus = "Connection settings invalid.";
            return;
        }

        await DisposeRuntimeClientAsync().ConfigureAwait(false);

        try
        {
            var runtimeClient = await BasicsRuntimeClient.StartAsync(options).ConfigureAwait(false);
            var ack = await runtimeClient.ConnectAsync(ClientName.Trim()).ConfigureAwait(false);
            if (ack is null)
            {
                await runtimeClient.DisposeAsync().ConfigureAwait(false);
                _dispatcher.Post(() => ConnectionStatus = "IO connect failed.");
                return;
            }

            _runtimeClient = runtimeClient;
            _dispatcher.Post(() =>
            {
                ConnectionStatus = $"Connected to {IoAddress} as {ack.ServerName}";
                CapacityStatus = "Connected. Fetching capacity through IO...";
                RaiseCommandStates();
            });

            await FetchCapacityAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => ConnectionStatus = $"IO connect failed: {ex.GetBaseException().Message}");
        }
    }

    private async Task DisconnectAsync()
    {
        await StopExecutionAsync().ConfigureAwait(false);
        await DisposeRuntimeClientAsync().ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            ConnectionStatus = "Disconnected";
            CapacityStatus = "No capacity snapshot fetched.";
            ExecutionStatus = "Idle";
            ExecutionDetail = "Connect to IO, build capacity bounds, then start an implemented task plugin.";
            RaiseCommandStates();
        });
    }

    private async Task FetchCapacityAsync()
    {
        if (_runtimeClient is null)
        {
            CapacityStatus = "Connect to IO before fetching capacity.";
            return;
        }

        if (!TryBuildEnvironmentOptions(out var options, out var errors))
        {
            ApplyValidationMessages(errors);
            CapacityStatus = "Configuration invalid.";
            return;
        }

        try
        {
            var planner = new BasicsEnvironmentPlanner(_runtimeClient);
            var plan = await planner.BuildPlanAsync(options).ConfigureAwait(false);
            _dispatcher.Post(() => ApplyPlan(plan));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => CapacityStatus = $"Plan failed: {ex.GetBaseException().Message}");
        }
    }

    private void ApplySuggestedBounds()
    {
        if (_lastPlan is null)
        {
            return;
        }

        _suppressValidationRefresh = true;
        try
        {
            InitialPopulationOverrideText = _lastPlan.Capacity.RecommendedInitialPopulationCount.ToString(CultureInfo.InvariantCulture);
            ReproductionRunCountOverrideText = _lastPlan.Capacity.RecommendedReproductionRunCount.ToString(CultureInfo.InvariantCulture);
            MaxConcurrentBrainsOverrideText = _lastPlan.Capacity.RecommendedMaxConcurrentBrains.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressValidationRefresh = false;
        }

        RefreshValidationState();
    }

    private void ApplyTaskDefaults(TaskOption? task)
    {
        if (task is null)
        {
            return;
        }

        var profile = BasicsTaskExecutionProfiles.Resolve(task.TaskId);
        _suppressValidationRefresh = true;
        try
        {
            MaxInternalNeuronDeltaText = profile.VariationBand.MaxInternalNeuronDelta.ToString(CultureInfo.InvariantCulture);
            MaxAxonDeltaText = profile.VariationBand.MaxAxonDelta.ToString(CultureInfo.InvariantCulture);
            MaxStrengthCodeDeltaText = profile.VariationBand.MaxStrengthCodeDelta.ToString(CultureInfo.InvariantCulture);
            MaxParameterCodeDeltaText = profile.VariationBand.MaxParameterCodeDelta.ToString(CultureInfo.InvariantCulture);
            AllowFunctionMutation = profile.VariationBand.AllowFunctionMutation;
            AllowAxonReroute = profile.VariationBand.AllowAxonReroute;
            AllowRegionSetChange = profile.VariationBand.AllowRegionSetChange;

            MinActiveInternalRegionCountText = FormatOptionalInt(profile.SeedShape.MinActiveInternalRegionCount);
            MaxActiveInternalRegionCountText = FormatOptionalInt(profile.SeedShape.MaxActiveInternalRegionCount);
            MinInternalNeuronCountText = FormatOptionalInt(profile.SeedShape.MinInternalNeuronCount);
            MaxInternalNeuronCountText = FormatOptionalInt(profile.SeedShape.MaxInternalNeuronCount);
            MinAxonCountText = FormatOptionalInt(profile.SeedShape.MinAxonCount);
            MaxAxonCountText = FormatOptionalInt(profile.SeedShape.MaxAxonCount);

            InitialPopulationOverrideText = FormatOptionalInt(profile.Sizing.InitialPopulationCount);
            ReproductionRunCountOverrideText = FormatOptionalUInt(profile.Sizing.ReproductionRunCount);
            MaxConcurrentBrainsOverrideText = FormatOptionalInt(profile.Sizing.MaxConcurrentBrains);

            FitnessWeightText = profile.Scheduling.ParentSelection.FitnessWeight.ToString("0.##", CultureInfo.InvariantCulture);
            DiversityWeightText = profile.Scheduling.ParentSelection.DiversityWeight.ToString("0.##", CultureInfo.InvariantCulture);
            SpeciesBalanceWeightText = profile.Scheduling.ParentSelection.SpeciesBalanceWeight.ToString("0.##", CultureInfo.InvariantCulture);
            EliteFractionText = profile.Scheduling.ParentSelection.EliteFraction.ToString("0.##", CultureInfo.InvariantCulture);
            ExplorationFractionText = profile.Scheduling.ParentSelection.ExplorationFraction.ToString("0.##", CultureInfo.InvariantCulture);
            MaxParentsPerSpeciesText = profile.Scheduling.ParentSelection.MaxParentsPerSpecies.ToString(CultureInfo.InvariantCulture);
            MinRunsPerPairText = profile.Scheduling.RunAllocation.MinRunsPerPair.ToString(CultureInfo.InvariantCulture);
            MaxRunsPerPairText = profile.Scheduling.RunAllocation.MaxRunsPerPair.ToString(CultureInfo.InvariantCulture);
            FitnessExponentText = profile.Scheduling.RunAllocation.FitnessExponent.ToString("0.##", CultureInfo.InvariantCulture);
            DiversityBoostText = profile.Scheduling.RunAllocation.DiversityBoost.ToString("0.##", CultureInfo.InvariantCulture);

            SelectedOutputObservationMode = OutputObservationModes.FirstOrDefault(option => option.Mode == profile.OutputObservationMode)
                ?? SelectedOutputObservationMode;
        }
        finally
        {
            _suppressValidationRefresh = false;
        }

        RefreshValidationState();
    }

    private async Task StartAsync()
    {
        try
        {
            if (_runtimeClient is null)
            {
                ExecutionStatus = "Start blocked.";
                ExecutionDetail = "Connect to IO before starting a run.";
                return;
            }

            var runtimeOptionsValid = TryBuildRuntimeClientOptions(out var runtimeOptions, out var runtimeErrors);
            var environmentOptionsValid = TryBuildEnvironmentOptions(out var options, out var optionErrors);
            if (!runtimeOptionsValid || !environmentOptionsValid)
            {
                ApplyValidationMessages(runtimeErrors.Concat(optionErrors).ToArray());
                ExecutionStatus = "Start blocked.";
                ExecutionDetail = "Fix validation issues before starting a run.";
                return;
            }

            var planner = new BasicsEnvironmentPlanner(_runtimeClient);
            var plan = await planner.BuildPlanAsync(options).ConfigureAwait(false);
            _dispatcher.Post(() => ApplyPlan(plan));

            if (!TaskPluginRegistry.TryGet(plan.SelectedTask.TaskId, out var plugin))
            {
                _dispatcher.Post(() =>
                {
                    ExecutionStatus = "Start blocked.";
                    ExecutionDetail = $"{plan.SelectedTask.DisplayName} is not implemented yet.";
                });
                return;
            }

            await StopExecutionAsync().ConfigureAwait(false);
            _dispatcher.Post(() => ClearWinnerState(clearArtifacts: true));

            var session = new BasicsExecutionSession(
                _runtimeClient,
                new BasicsTemplatePublishingOptions
                {
                    BindHost = runtimeOptions.BindHost,
                    AdvertiseHost = runtimeOptions.AdvertiseHost
                });

            _executionSession = session;
            _executionCts = new CancellationTokenSource();
                _dispatcher.Post(() =>
                {
                    ResetCharts();
                    IsExecutionRunning = true;
                    ExecutionStatus = "Starting...";
                    ExecutionDetail = $"Launching {plan.SelectedTask.DisplayName} with template family {plan.SeedTemplate.TemplateId}. Stop target: accuracy >= {plan.StopCriteria.TargetAccuracy:0.###}, fitness >= {plan.StopCriteria.TargetFitness:0.###}.";
                    RaiseCommandStates();
                });

            try
            {
                await session.RunAsync(
                        plan,
                        plugin,
                        snapshot => _dispatcher.Post(() => ApplyExecutionSnapshot(snapshot, plan.Capacity.RecommendedMaxConcurrentBrains)),
                        _executionCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_executionCts.IsCancellationRequested)
            {
                // Normal stop path.
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
                _executionSession = null;
                _executionCts?.Dispose();
                _executionCts = null;
                _dispatcher.Post(() =>
                {
                    IsExecutionRunning = false;
                    RaiseCommandStates();
                });
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                ExecutionStatus = "Execution failed.";
                ExecutionDetail = ex.GetBaseException().Message;
            });
        }
    }

    private Task StopAsync() => StopExecutionAsync();

    private void ApplyPlan(BasicsEnvironmentPlan plan)
    {
        _lastPlan = plan;
        RecommendedInitialPopulationText = plan.Capacity.RecommendedInitialPopulationCount.ToString(CultureInfo.InvariantCulture);
        RecommendedRunCountText = plan.Capacity.RecommendedReproductionRunCount.ToString(CultureInfo.InvariantCulture);
        RecommendedMaxConcurrentBrainsText = plan.Capacity.RecommendedMaxConcurrentBrains.ToString(CultureInfo.InvariantCulture);
        CapacityStatus = $"Capacity source: {plan.Capacity.Source}";
        CapacitySummary = plan.Capacity.Summary;
        LastPlanSummary = $"Task {plan.SelectedTask.DisplayName} · template {plan.SeedTemplate.TemplateId} · {plan.Capacity.EligibleWorkerCount} eligible worker(s).";
        MetricsStatus = TaskPluginRegistry.TryGet(plan.SelectedTask.TaskId, out _)
            ? $"{plan.SelectedTask.DisplayName} plugin is available; Start will seed an artifact pool, evaluate live brains, and update metrics here."
            : $"{plan.SelectedTask.DisplayName} plugin is not implemented yet.";
        MetricsSecondaryStatus = $"Population {plan.Capacity.RecommendedInitialPopulationCount}, concurrent {plan.Capacity.RecommendedMaxConcurrentBrains}, run count {plan.Capacity.RecommendedReproductionRunCount}, output mode {FormatOutputObservationMode(plan.OutputObservationMode)}, stop target {plan.StopCriteria.TargetAccuracy:0.###}/{plan.StopCriteria.TargetFitness:0.###}.";
        if (!IsExecutionRunning)
        {
            ExecutionStatus = "Ready to start.";
            ExecutionDetail = TaskPluginRegistry.TryGet(plan.SelectedTask.TaskId, out _)
                ? $"Template {plan.SeedTemplate.TemplateId} will be published automatically if no artifact ref is supplied. Output mode: {FormatOutputObservationMode(plan.OutputObservationMode)}. Stop target: accuracy >= {plan.StopCriteria.TargetAccuracy:0.###}, fitness >= {plan.StopCriteria.TargetFitness:0.###}."
                : $"{plan.SelectedTask.DisplayName} cannot start until its plugin issue is implemented.";
        }

        UpdateMetricSummary(BasicsMetricId.PopulationCount, plan.Capacity.RecommendedInitialPopulationCount.ToString(CultureInfo.InvariantCulture), "Recommended initial population bound.");
        UpdateMetricSummary(BasicsMetricId.ActiveBrainCount, plan.Capacity.RecommendedMaxConcurrentBrains.ToString(CultureInfo.InvariantCulture), "Recommended max concurrent brains.");
        UpdateMetricSummary(BasicsMetricId.ReproductionRunsObserved, plan.Capacity.RecommendedReproductionRunCount.ToString(CultureInfo.InvariantCulture), "Recommended runs per parent pair.");
        UpdateMetricSummary(BasicsMetricId.SpeciesCount, plan.SeedTemplate.ExpectSingleBootstrapSpecies ? "1 seed family" : "multiple", "Bootstrap template-family expectation.");
        UpdateMetricSummary(BasicsMetricId.CapacityUtilization, plan.Capacity.CapacityScore.ToString("0.###", CultureInfo.InvariantCulture), plan.Capacity.Source.ToString());
        RaiseCommandStates();
    }

    private void RefreshValidationState()
    {
        var errors = new List<string>();
        TryBuildRuntimeClientOptions(out _, out var runtimeErrors);
        errors.AddRange(runtimeErrors);
        TryBuildEnvironmentOptions(out _, out var optionErrors);
        errors.AddRange(optionErrors);
        ApplyValidationMessages(errors);
        RaiseCommandStates();
    }

    private void ApplyValidationMessages(IReadOnlyCollection<string> errors)
    {
        ValidationErrors.Clear();
        foreach (var error in errors)
        {
            ValidationErrors.Add(error);
        }

        ValidationSummary = errors.Count == 0
            ? "Configuration valid."
            : $"{errors.Count} validation issue(s) blocking plan/build actions.";
    }

    private bool CanConnect()
    {
        return TryBuildRuntimeClientOptions(out _, out _)
               && !IsExecutionRunning
               && !string.IsNullOrWhiteSpace(ClientName);
    }

    private bool CanBuildPlan()
    {
        return _runtimeClient is not null
               && !IsExecutionRunning
               && TryBuildEnvironmentOptions(out _, out _);
    }

    private bool CanDisconnect() => _runtimeClient is not null && !IsExecutionRunning;

    private bool CanStart()
    {
        return _runtimeClient is not null
               && !IsExecutionRunning
               && TryBuildEnvironmentOptions(out _, out _)
               && SelectedTask is not null
               && TaskPluginRegistry.TryGet(SelectedTask.TaskId, out _);
    }

    private bool CanStop() => (IsExecutionRunning && _executionCts is not null) || _retainedWinnerBrainId != Guid.Empty;

    private bool CanExportWinnerDefinition() => HasArtifactRef(_winnerDefinitionArtifact);

    private bool CanExportWinnerSnapshot() => HasArtifactRef(_winnerSnapshotArtifact);

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        FetchCapacityCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ExportDefinitionCommand.RaiseCanExecuteChanged();
        ExportSnapshotCommand.RaiseCanExecuteChanged();
        ApplySuggestedBoundsCommand.RaiseCanExecuteChanged();
    }

    private bool TryBuildRuntimeClientOptions(
        out BasicsRuntimeClientOptions options,
        out List<string> errors)
    {
        errors = new List<string>();
        options = new BasicsRuntimeClientOptions();

        if (string.IsNullOrWhiteSpace(IoAddress))
        {
            errors.Add("IO address is required.");
        }

        if (string.IsNullOrWhiteSpace(IoGatewayName))
        {
            errors.Add("IO gateway name is required.");
        }

        if (string.IsNullOrWhiteSpace(ClientName))
        {
            errors.Add("Client name is required.");
        }

        if (!int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
        {
            errors.Add("Bind port must be a positive integer.");
        }

        int? advertisePort = null;
        if (!string.IsNullOrWhiteSpace(AdvertisePortText))
        {
            if (!int.TryParse(AdvertisePortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAdvertisePort) || parsedAdvertisePort <= 0)
            {
                errors.Add("Advertise port must be empty or a positive integer.");
            }
            else
            {
                advertisePort = parsedAdvertisePort;
            }
        }

        if (!double.TryParse(RequestTimeoutSecondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var requestTimeoutSeconds) || requestTimeoutSeconds <= 0d)
        {
            errors.Add("Request timeout must be a positive number of seconds.");
        }

        if (errors.Count != 0)
        {
            return false;
        }

        options = new BasicsRuntimeClientOptions
        {
            IoAddress = IoAddress.Trim(),
            IoGatewayName = IoGatewayName.Trim(),
            BindHost = string.IsNullOrWhiteSpace(BindHost) ? NetworkAddressDefaults.DefaultBindHost : BindHost.Trim(),
            Port = port,
            AdvertiseHost = string.IsNullOrWhiteSpace(AdvertiseHost) ? null : AdvertiseHost.Trim(),
            AdvertisePort = advertisePort,
            RequestTimeout = TimeSpan.FromSeconds(requestTimeoutSeconds)
        };
        return true;
    }

    private bool TryBuildEnvironmentOptions(
        out BasicsEnvironmentOptions options,
        out List<string> errors)
    {
        errors = new List<string>();
        options = new BasicsEnvironmentOptions();

        var templateDefinition = TryBuildTemplateArtifact(out var templateErrors);
        errors.AddRange(templateErrors);

        var variation = new BasicsSeedVariationBand
        {
            MaxInternalNeuronDelta = ParseRequiredInt(MaxInternalNeuronDeltaText, "Internal neuron delta", errors),
            MaxAxonDelta = ParseRequiredInt(MaxAxonDeltaText, "Axon delta", errors),
            MaxStrengthCodeDelta = ParseRequiredInt(MaxStrengthCodeDeltaText, "Strength-code delta", errors),
            MaxParameterCodeDelta = ParseRequiredInt(MaxParameterCodeDeltaText, "Parameter-code delta", errors),
            AllowFunctionMutation = AllowFunctionMutation,
            AllowAxonReroute = AllowAxonReroute,
            AllowRegionSetChange = AllowRegionSetChange
        };

        var seedShapeConstraints = new BasicsSeedShapeConstraints
        {
            MinActiveInternalRegionCount = ParseOptionalInt(MinActiveInternalRegionCountText, "Minimum internal region count", errors),
            MaxActiveInternalRegionCount = ParseOptionalInt(MaxActiveInternalRegionCountText, "Maximum internal region count", errors),
            MinInternalNeuronCount = ParseOptionalInt(MinInternalNeuronCountText, "Minimum internal neuron count", errors),
            MaxInternalNeuronCount = ParseOptionalInt(MaxInternalNeuronCountText, "Maximum internal neuron count", errors),
            MinAxonCount = ParseOptionalInt(MinAxonCountText, "Minimum axon count", errors),
            MaxAxonCount = ParseOptionalInt(MaxAxonCountText, "Maximum axon count", errors)
        };

        var overrides = new BasicsSizingOverrides
        {
            InitialPopulationCount = ParseOptionalInt(InitialPopulationOverrideText, "Initial population override", errors),
            ReproductionRunCount = ParseOptionalUInt(ReproductionRunCountOverrideText, "Run-count override", errors),
            MaxConcurrentBrains = ParseOptionalInt(MaxConcurrentBrainsOverrideText, "Max-concurrent override", errors)
        };

        var scheduling = new BasicsReproductionSchedulingPolicy
        {
            ParentSelection = new BasicsParentSelectionPolicy
            {
                FitnessWeight = ParseRequiredDouble(FitnessWeightText, "Fitness weight", errors),
                DiversityWeight = ParseRequiredDouble(DiversityWeightText, "Diversity weight", errors),
                SpeciesBalanceWeight = ParseRequiredDouble(SpeciesBalanceWeightText, "Species-balance weight", errors),
                EliteFraction = ParseRequiredDouble(EliteFractionText, "Elite fraction", errors),
                ExplorationFraction = ParseRequiredDouble(ExplorationFractionText, "Exploration fraction", errors),
                MaxParentsPerSpecies = ParseRequiredInt(MaxParentsPerSpeciesText, "Max parents per species", errors)
            },
            RunAllocation = new BasicsRunAllocationPolicy
            {
                MinRunsPerPair = ParseRequiredUInt(MinRunsPerPairText, "Min runs per pair", errors),
                MaxRunsPerPair = ParseRequiredUInt(MaxRunsPerPairText, "Max runs per pair", errors),
                FitnessExponent = ParseRequiredDouble(FitnessExponentText, "Fitness exponent", errors),
                DiversityBoost = ParseRequiredDouble(DiversityBoostText, "Diversity boost", errors)
            }
        };

        var seedTemplate = new BasicsSeedTemplateContract
        {
            TemplateId = TemplateId.Trim(),
            Description = TemplateDescription.Trim(),
            TemplateDefinition = templateDefinition,
            InitialVariationBand = variation,
            InitialSeedShapeConstraints = seedShapeConstraints
        };
        var stopCriteria = new BasicsExecutionStopCriteria
        {
            TargetAccuracy = ParseRequiredFloat(TargetAccuracyText, "Stop accuracy target", errors),
            TargetFitness = ParseRequiredFloat(TargetFitnessText, "Stop fitness target", errors)
        };

        options = new BasicsEnvironmentOptions
        {
            ClientName = ClientName.Trim(),
            SelectedTask = SelectedTask?.Contract ?? new BasicsTaskContract(
                TaskId: "and",
                DisplayName: "AND",
                InputWidth: BasicsIoGeometry.InputWidth,
                OutputWidth: BasicsIoGeometry.OutputWidth,
                UsesTickAlignedEvaluation: true,
                Description: "Boolean AND over canonical 0/1 inputs and outputs."),
            SeedTemplate = seedTemplate,
            SizingOverrides = overrides,
            OutputObservationMode = SelectedOutputObservationMode?.Mode ?? BasicsOutputObservationMode.VectorPotential,
            Reproduction = new BasicsReproductionPolicy
            {
                StrengthSource = SelectedStrengthSource?.Value ?? Repro.StrengthSource.StrengthBaseOnly
            },
            Scheduling = scheduling,
            StopCriteria = stopCriteria
        };

        var validation = options.Validate();
        if (!validation.IsValid)
        {
            errors.AddRange(validation.Errors);
        }

        return errors.Count == 0;
    }

    private ArtifactRef? TryBuildTemplateArtifact(out List<string> errors)
    {
        errors = new List<string>();
        if (string.IsNullOrWhiteSpace(TemplateArtifactSha256)
            && string.IsNullOrWhiteSpace(TemplateArtifactSizeBytesText)
            && string.IsNullOrWhiteSpace(TemplateArtifactStoreUri))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(TemplateArtifactSha256))
        {
            errors.Add("Template artifact SHA-256 is required when any template artifact field is set.");
            return null;
        }

        if (!ProtoSha256Extensions.TryFromHex(TemplateArtifactSha256.Trim(), out var sha256))
        {
            errors.Add("Template artifact SHA-256 must be a valid 64-character hex value.");
            return null;
        }

        ulong sizeBytes = 0;
        if (!string.IsNullOrWhiteSpace(TemplateArtifactSizeBytesText)
            && (!ulong.TryParse(TemplateArtifactSizeBytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out sizeBytes)))
        {
            errors.Add("Template artifact size bytes must be empty or a non-negative integer.");
        }

        var mediaType = string.IsNullOrWhiteSpace(TemplateArtifactMediaType)
            ? "application/x-nbn"
            : TemplateArtifactMediaType.Trim();
        return sha256.ToArtifactRef(
            sizeBytes: sizeBytes,
            mediaType: mediaType,
            storeUri: string.IsNullOrWhiteSpace(TemplateArtifactStoreUri) ? null : TemplateArtifactStoreUri.Trim());
    }

    private async Task StopExecutionAsync()
    {
        if (_executionCts is not null)
        {
            _dispatcher.Post(() =>
            {
                ExecutionStatus = "Stopping...";
                ExecutionDetail = "Canceling the current run and cleaning up evaluation brains.";
                RaiseCommandStates();
            });

            _executionCts.Cancel();
            await Task.Yield();
            return;
        }

        if (_retainedWinnerBrainId != Guid.Empty)
        {
            _dispatcher.Post(() =>
            {
                ExecutionStatus = "Stopping...";
                ExecutionDetail = "Releasing the retained winning brain.";
                RaiseCommandStates();
            });

            await ReleaseRetainedWinnerAsync("basics_ui_release_winner").ConfigureAwait(false);
        }
    }

    private void ApplyExecutionSnapshot(BasicsExecutionSnapshot snapshot, int maxConcurrentBrains)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var detailLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.DetailText))
        {
            detailLines.Add(snapshot.DetailText);
        }

        if (snapshot.SpeciationEpochId is ulong epochId && epochId > 0)
        {
            detailLines.Add($"Speciation epoch {epochId}.");
        }

        if (snapshot.EvaluationFailureCount > 0 && !string.IsNullOrWhiteSpace(snapshot.EvaluationFailureSummary))
        {
            detailLines.Add($"Evaluation failures: {snapshot.EvaluationFailureSummary}");
        }

        if (snapshot.BestCandidate is not null && snapshot.BestCandidate.Diagnostics.Count > 0)
        {
            detailLines.Add($"Best-candidate diagnostics: {string.Join("; ", snapshot.BestCandidate.Diagnostics.Take(3))}");
        }

        var resolvedDetail = string.Join(" ", detailLines);
        ExecutionStatus = snapshot.StatusText;
        ExecutionDetail = resolvedDetail;
        MetricsStatus = snapshot.StatusText;
        MetricsSecondaryStatus = resolvedDetail;
        LastPlanSummary = $"Generation {snapshot.Generation} · population {snapshot.PopulationCount} · species {snapshot.SpeciesCount}.";

        ReplaceHistory(_accuracyHistory, snapshot.AccuracyHistory);
        ReplaceHistory(_fitnessHistory, snapshot.BestFitnessHistory);
        UpdateChartBindings();

        if (snapshot.EffectiveTemplateDefinition is not null)
        {
            _suppressValidationRefresh = true;
            try
            {
                TemplateArtifactSha256 = snapshot.EffectiveTemplateDefinition.ToSha256Hex();
                TemplateArtifactMediaType = string.IsNullOrWhiteSpace(snapshot.EffectiveTemplateDefinition.MediaType)
                    ? "application/x-nbn"
                    : snapshot.EffectiveTemplateDefinition.MediaType;
                TemplateArtifactSizeBytesText = snapshot.EffectiveTemplateDefinition.SizeBytes.ToString(CultureInfo.InvariantCulture);
                TemplateArtifactStoreUri = snapshot.EffectiveTemplateDefinition.StoreUri ?? string.Empty;
            }
            finally
            {
                _suppressValidationRefresh = false;
            }
        }

        UpdateMetricSummary(
            BasicsMetricId.Accuracy,
            snapshot.BestAccuracy.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null
                ? "No successful evaluation yet."
                : $"Best candidate species {snapshot.BestCandidate.SpeciesId}.");
        UpdateMetricSummary(
            BasicsMetricId.BestFitness,
            snapshot.BestFitness.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null
                ? "No successful evaluation yet."
                : $"Artifact {snapshot.BestCandidate.ArtifactSha256[..Math.Min(12, snapshot.BestCandidate.ArtifactSha256.Length)]}...");
        UpdateMetricSummary(
            BasicsMetricId.MeanFitness,
            snapshot.MeanFitness.ToString("0.###", CultureInfo.InvariantCulture),
            $"Generation {snapshot.Generation} mean fitness.");
        UpdateMetricSummary(
            BasicsMetricId.PopulationCount,
            snapshot.PopulationCount.ToString(CultureInfo.InvariantCulture),
            $"Artifact pool size for generation {snapshot.Generation}.");
        UpdateMetricSummary(
            BasicsMetricId.ActiveBrainCount,
            snapshot.ActiveBrainCount.ToString(CultureInfo.InvariantCulture),
            $"Current active evaluation brains (cap {maxConcurrentBrains}).");
        UpdateMetricSummary(
            BasicsMetricId.SpeciesCount,
            snapshot.SpeciesCount.ToString(CultureInfo.InvariantCulture),
            snapshot.SpeciationEpochId is ulong currentEpochId && currentEpochId > 0
                ? $"Current committed speciation memberships in epoch {currentEpochId}."
                : "Current committed speciation memberships in the artifact pool.");
        UpdateMetricSummary(
            BasicsMetricId.ReproductionCalls,
            snapshot.ReproductionCalls.ToString(CultureInfo.InvariantCulture),
            "Cumulative reproduction requests sent through IO.");
        UpdateMetricSummary(
            BasicsMetricId.ReproductionRunsObserved,
            snapshot.ReproductionRunsObserved.ToString(CultureInfo.InvariantCulture),
            "Cumulative requested/effective child runs.");
        UpdateMetricSummary(
            BasicsMetricId.CapacityUtilization,
            snapshot.CapacityUtilization.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.ActiveBrainCount > 0
                ? "Current evaluation batch utilization."
                : "Idle between evaluation batches.");

        if (snapshot.State == BasicsExecutionState.Succeeded && snapshot.BestCandidate is not null)
        {
            ApplyWinnerArtifacts(snapshot.BestCandidate);
        }
        else if (snapshot.State is BasicsExecutionState.Failed or BasicsExecutionState.Stopped)
        {
            ClearWinnerState(clearArtifacts: false);
        }
    }

    private async Task ExportWinningDefinitionAsync()
    {
        if (!HasArtifactRef(_winnerDefinitionArtifact))
        {
            WinnerExportStatus = "No winning definition available.";
            WinnerExportDetail = "Run a session to a stop target before exporting.";
            return;
        }

        try
        {
            var path = await _artifactExportService.ExportAsync(
                    _winnerDefinitionArtifact!,
                    title: "Export winning definition",
                    suggestedFileName: BuildSuggestedArtifactFileName("winner", "nbn"))
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                WinnerExportStatus = path is null ? "Definition export canceled." : "Winning definition exported.";
                WinnerExportDetail = path is null
                    ? "The winning .nbn artifact is still available for export."
                    : path;
                RaiseCommandStates();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                WinnerExportStatus = "Definition export failed.";
                WinnerExportDetail = ex.GetBaseException().Message;
                RaiseCommandStates();
            });
        }
    }

    private async Task ExportWinningSnapshotAsync()
    {
        if (!HasArtifactRef(_winnerSnapshotArtifact))
        {
            WinnerExportStatus = "No winning snapshot available.";
            WinnerExportDetail = "Snapshot export is only available when runtime state was captured for the retained winner.";
            return;
        }

        try
        {
            var path = await _artifactExportService.ExportAsync(
                    _winnerSnapshotArtifact!,
                    title: "Export winning snapshot",
                    suggestedFileName: BuildSuggestedArtifactFileName("winner-state", "nbs"))
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                WinnerExportStatus = path is null ? "Snapshot export canceled." : "Winning snapshot exported.";
                WinnerExportDetail = path is null
                    ? "The winning .nbs artifact is still available for export."
                    : path;
                RaiseCommandStates();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                WinnerExportStatus = "Snapshot export failed.";
                WinnerExportDetail = ex.GetBaseException().Message;
                RaiseCommandStates();
            });
        }
    }

    private async Task ReleaseRetainedWinnerAsync(string reason)
    {
        var retainedBrainId = _retainedWinnerBrainId;
        _retainedWinnerBrainId = Guid.Empty;

        if (retainedBrainId != Guid.Empty && _runtimeClient is not null)
        {
            try
            {
                await _runtimeClient.KillBrainAsync(retainedBrainId, reason).ConfigureAwait(false);
                await _runtimeClient.WaitForBrainTerminatedAsync(retainedBrainId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        _dispatcher.Post(() =>
        {
            UpdateWinnerExportSummary();
            RaiseCommandStates();
        });
    }

    private void ApplyWinnerArtifacts(BasicsExecutionBestCandidateSummary winner)
    {
        _winnerDefinitionArtifact = winner.DefinitionArtifact.Clone();
        _winnerSnapshotArtifact = winner.SnapshotArtifact?.Clone();
        _retainedWinnerBrainId = winner.ActiveBrainId ?? Guid.Empty;
        UpdateWinnerExportSummary();
        RaiseCommandStates();
    }

    private void ClearWinnerState(bool clearArtifacts)
    {
        _retainedWinnerBrainId = Guid.Empty;
        if (clearArtifacts)
        {
            _winnerDefinitionArtifact = null;
            _winnerSnapshotArtifact = null;
        }

        UpdateWinnerExportSummary();
        RaiseCommandStates();
    }

    private void UpdateWinnerExportSummary()
    {
        if (!HasArtifactRef(_winnerDefinitionArtifact))
        {
            WinnerExportStatus = "No winning brain retained.";
            WinnerExportDetail = "When a run meets the stop target, the simplest qualifying winner stays active for export.";
            return;
        }

        WinnerExportStatus = _retainedWinnerBrainId != Guid.Empty
            ? "Winning brain retained for export."
            : "Winner artifacts retained.";

        var detailParts = new List<string>
        {
            $"Definition .nbn ready ({ShortArtifactSha(_winnerDefinitionArtifact)})."
        };
        if (HasArtifactRef(_winnerSnapshotArtifact))
        {
            detailParts.Add($"Snapshot .nbs ready ({ShortArtifactSha(_winnerSnapshotArtifact)}).");
        }
        else
        {
            detailParts.Add("No snapshot artifact was produced for this winner.");
        }

        if (_retainedWinnerBrainId != Guid.Empty)
        {
            detailParts.Add($"Live winner {_retainedWinnerBrainId:N} stays active until Stop, Disconnect, or the next run.");
        }

        WinnerExportDetail = string.Join(' ', detailParts);
    }

    private async Task DisposeRuntimeClientAsync()
    {
        if (_runtimeClient is null)
        {
            return;
        }

        var current = _runtimeClient;
        _runtimeClient = null;
        await current.DisposeAsync().ConfigureAwait(false);
    }

    private string BuildSuggestedArtifactFileName(string suffix, string extension)
    {
        var taskId = SelectedTask?.TaskId ?? _lastPlan?.SelectedTask.TaskId ?? "basics";
        return $"basics-{taskId}-{suffix}.{extension}";
    }

    private static string ShortArtifactSha(ArtifactRef? artifact)
    {
        if (!HasArtifactRef(artifact))
        {
            return "unknown";
        }

        var sha = artifact!.ToSha256Hex();
        return sha[..Math.Min(12, sha.Length)];
    }

    private static bool HasArtifactRef(ArtifactRef? artifact)
        => artifact is not null && artifact.TryToSha256Bytes(out _);

    private static int ParseRequiredInt(string text, string fieldName, ICollection<string> errors)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{fieldName} must be an integer.");
            return 0;
        }

        return value;
    }

    private static uint ParseRequiredUInt(string text, string fieldName, ICollection<string> errors)
    {
        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{fieldName} must be a positive integer.");
            return 0;
        }

        return value;
    }

    private static double ParseRequiredDouble(string text, string fieldName, ICollection<string> errors)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{fieldName} must be a number.");
            return 0d;
        }

        return value;
    }

    private static float ParseRequiredFloat(string text, string fieldName, ICollection<string> errors)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{fieldName} must be a number.");
            return 0f;
        }

        return value;
    }

    private static int? ParseOptionalInt(string text, string fieldName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{fieldName} must be empty or an integer.");
            return null;
        }

        return value;
    }

    private static uint? ParseOptionalUInt(string text, string fieldName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{fieldName} must be empty or a positive integer.");
            return null;
        }

        return value;
    }

    private static string FormatOptionalInt(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatOptionalUInt(uint? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private void ResetCharts()
    {
        _accuracyHistory.Clear();
        _fitnessHistory.Clear();
        UpdateChartBindings();
    }

    private void ReplaceHistory(List<float> target, IReadOnlyList<float> source)
    {
        target.Clear();
        target.AddRange(source);
    }

    private void UpdateChartBindings()
    {
        OnPropertyChanged(nameof(HasAccuracyChartData));
        OnPropertyChanged(nameof(HasFitnessChartData));
        OnPropertyChanged(nameof(ShowAccuracyEmptyState));
        OnPropertyChanged(nameof(ShowFitnessEmptyState));
        OnPropertyChanged(nameof(AccuracyChartPoints));
        OnPropertyChanged(nameof(FitnessChartPoints));
    }

    private static string BuildChartPoints(IReadOnlyList<float> history)
    {
        if (history.Count == 0)
        {
            return string.Empty;
        }

        const float width = 320f;
        const float height = 140f;
        if (history.Count == 1)
        {
            var y = height - (Math.Clamp(history[0], 0f, 1f) * height);
            return $"0,{y.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        var stepX = width / (history.Count - 1f);
        var points = history.Select((value, index) =>
        {
            var x = index * stepX;
            var y = height - (Math.Clamp(value, 0f, 1f) * height);
            return $"{x.ToString("0.###", CultureInfo.InvariantCulture)},{y.ToString("0.###", CultureInfo.InvariantCulture)}";
        });
        return string.Join(' ', points);
    }

    private void UpdateMetricSummary(BasicsMetricId metricId, string value, string detail)
    {
        var item = MetricSummaries.FirstOrDefault(entry => entry.MetricId == metricId);
        if (item is null)
        {
            return;
        }

        item.ValueText = value;
        item.DetailText = detail;
    }

    private static IEnumerable<TaskOption> BuildTasks()
    {
        yield return CreateTaskOption("and", "AND", "Boolean AND over canonical 0/1 inputs and outputs.", "Plugin pending");
        yield return CreateTaskOption("or", "OR", "Boolean OR over canonical 0/1 inputs and outputs.", "Plugin pending");
        yield return CreateTaskOption("xor", "XOR", "Boolean XOR over canonical 0/1 inputs and outputs.", "Plugin pending");
        yield return CreateTaskOption("gt", "GT", "Boolean greater-than over canonical 0/1 or bounded scalar inputs.", "Plugin pending");
        yield return CreateTaskOption("multiplication", "Multiplication", "Bounded scalar multiplication over the shared 2->1 Basics geometry.", "Plugin pending");
        yield return CreateTaskOption("denoise", "Noisy in → clean out", "Denoising task over the shared 2->1 Basics geometry.", "Plugin pending");
        yield return CreateTaskOption("delay", "Delayed out", "Temporal delayed-output task over the shared 2->1 Basics geometry.", "Plugin pending");
        yield return CreateTaskOption("one-hot", "One-hot classifier", "Viability still pending for the shared 2->1 Basics geometry.", "Viability pending");
    }

    private static TaskOption CreateTaskOption(string taskId, string displayName, string description, string placeholderStatus)
    {
        if (TaskPluginRegistry.TryGet(taskId, out var plugin))
        {
            return new TaskOption(plugin.Contract, "Plugin available");
        }

        return new TaskOption(
            new BasicsTaskContract(
                TaskId: taskId,
                DisplayName: displayName,
                InputWidth: BasicsIoGeometry.InputWidth,
                OutputWidth: BasicsIoGeometry.OutputWidth,
                UsesTickAlignedEvaluation: true,
                Description: description),
            placeholderStatus);
    }

    private static IEnumerable<StrengthSourceOption> BuildStrengthSources()
    {
        yield return new StrengthSourceOption(Repro.StrengthSource.StrengthBaseOnly, "Base definition only");
        yield return new StrengthSourceOption(Repro.StrengthSource.StrengthLiveCodes, "Base + live overlay codes");
    }

    private static IEnumerable<OutputObservationModeOption> BuildOutputObservationModes()
    {
        yield return new OutputObservationModeOption(
            BasicsOutputObservationMode.VectorPotential,
            "Continuous potential",
            "Recommended default for automated scoring; full vector every tick from activation/potential, applied per spawned brain.");
        yield return new OutputObservationModeOption(
            BasicsOutputObservationMode.EventedOutput,
            "OutputEvent",
            "Sparse fire-only outputs; zeros are inferred when no event arrives.");
        yield return new OutputObservationModeOption(
            BasicsOutputObservationMode.VectorBuffer,
            "Continuous buffer",
            "Full vector every tick from persistent buffer values, applied per spawned brain.");
    }

    private static IEnumerable<MetricSummaryItemViewModel> BuildMetricSummaryItems()
    {
        yield return new MetricSummaryItemViewModel(BasicsMetricId.Accuracy, "Accuracy", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestFitness, "Best fitness", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.MeanFitness, "Mean fitness", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.PopulationCount, "Population", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ActiveBrainCount, "Active brains", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.SpeciesCount, "Species", "1 seed family", "Template-anchored bootstrap assumption.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ReproductionCalls, "Reproduction calls", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ReproductionRunsObserved, "Runs per pair", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.CapacityUtilization, "Capacity score", "—", "Filled from IO capacity planning.");
    }

    private static string FormatOutputObservationMode(BasicsOutputObservationMode mode)
    {
        return mode switch
        {
            BasicsOutputObservationMode.EventedOutput => "OutputEvent",
            BasicsOutputObservationMode.VectorBuffer => "continuous buffer",
            _ => "continuous potential"
        };
    }
}

public sealed record TaskOption(BasicsTaskContract Contract, string StatusText)
{
    public string TaskId => Contract.TaskId;

    public string DisplayName => Contract.DisplayName;
}

public sealed record StrengthSourceOption(Repro.StrengthSource Value, string DisplayName);

public sealed record OutputObservationModeOption(
    BasicsOutputObservationMode Mode,
    string DisplayName,
    string DetailText);

public sealed class MetricSummaryItemViewModel : ViewModelBase
{
    private string _valueText;
    private string _detailText;

    public MetricSummaryItemViewModel(BasicsMetricId metricId, string displayName, string valueText, string detailText)
    {
        MetricId = metricId;
        DisplayName = displayName;
        _valueText = valueText;
        _detailText = detailText;
    }

    public BasicsMetricId MetricId { get; }

    public string DisplayName { get; }

    public string ValueText
    {
        get => _valueText;
        set => SetProperty(ref _valueText, value);
    }

    public string DetailText
    {
        get => _detailText;
        set => SetProperty(ref _detailText, value);
    }
}
