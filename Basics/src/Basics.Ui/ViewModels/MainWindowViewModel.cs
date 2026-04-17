using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Proto;
using Nbn.Shared;
using Repro = Nbn.Proto.Repro;

namespace Nbn.Demos.Basics.Ui.ViewModels;

public sealed record ChartAxisTickItem(
    double X,
    double LabelLeft,
    string LabelText);

public sealed record ChartAxisValueTickItem(
    double Y,
    double LabelTop,
    string LabelText);

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
        nameof(ShowAccuracyMetricToggles),
        nameof(ShowBalancedAccuracyChart),
        nameof(ShowEdgeAccuracyChart),
        nameof(ShowInteriorAccuracyChart),
        nameof(AccuracyChartValues),
        nameof(BestAccuracyChartValues),
        nameof(AccuracyTertiaryChartValues),
        nameof(AccuracyQuaternaryChartValues),
        nameof(FitnessChartValues),
        nameof(BestFitnessChartValues),
        nameof(FitnessTertiaryChartValues),
        nameof(FitnessQuaternaryChartValues),
        nameof(AccuracyChartPoints),
        nameof(BestAccuracyChartPoints),
        nameof(FitnessChartPoints),
        nameof(BestFitnessChartPoints),
        nameof(AccuracyGenerationTicks),
        nameof(FitnessGenerationTicks),
        nameof(ShowAccuracyStartGenerationTick),
        nameof(ShowAccuracyMidGenerationTick),
        nameof(ShowAccuracyEndGenerationTick),
        nameof(AccuracyStartGenerationTickLabel),
        nameof(AccuracyMidGenerationTickLabel),
        nameof(AccuracyEndGenerationTickLabel),
        nameof(ShowFitnessStartGenerationTick),
        nameof(ShowFitnessMidGenerationTick),
        nameof(ShowFitnessEndGenerationTick),
        nameof(FitnessStartGenerationTickLabel),
        nameof(FitnessMidGenerationTickLabel),
        nameof(FitnessEndGenerationTickLabel),
        nameof(ExecutionStatus),
        nameof(ExecutionDetail),
        nameof(ExecutionLogPath),
        nameof(IsExecutionRunning),
        nameof(MetricsStatus),
        nameof(MetricsSecondaryStatus),
        nameof(WinnerExportStatus),
        nameof(WinnerExportDetail),
        nameof(WorkerLauncherStatus),
        nameof(WorkerLauncherDetail),
        nameof(IsWorkerLauncherBusy)
    };

    private readonly UiDispatcher _dispatcher;
    private readonly IBasicsArtifactExportService _artifactExportService;
    private readonly IBasicsBrainImportService _brainImportService;
    private readonly IBasicsLocalWorkerProcessService _workerProcessService;
    private IBasicsRuntimeClient? _runtimeClient;
    private BasicsRuntimeClientOptions? _runtimeClientOptions;
    private BasicsExecutionSession? _executionSession;
    private CancellationTokenSource? _executionCts;
    private BasicsExecutionRunLog? _executionRunLog;
    private BasicsEnvironmentPlan? _lastPlan;
    private BasicsExecutionPlanTraceRecord? _lastPlanTrace;
    private BasicsBuildTraceRecord? _lastBuildTrace;
    private bool _suppressValidationRefresh;
    private bool _isExecutionRunning;
    private bool _isWorkerLauncherBusy;
    private readonly List<float> _accuracyHistory = new();
    private readonly List<float> _bestAccuracyHistory = new();
    private readonly List<float> _balancedAccuracyHistory = new();
    private readonly List<float> _bestBalancedAccuracyHistory = new();
    private readonly List<float> _edgeAccuracyHistory = new();
    private readonly List<float> _interiorAccuracyHistory = new();
    private readonly List<float> _fitnessHistory = new();
    private readonly List<float> _bestFitnessHistory = new();
    private readonly List<float> _behaviorOccupancyHistory = new();
    private readonly List<float> _behaviorPressureHistory = new();
    // Keep these in sync with the fixed plot host inside MainWindow.axaml.
    private const float ChartPlotWidth = 299f;
    private const float ChartPlotHeight = 414f;
    private const float ChartStrokeInset = 1f;
    private const int MaxGenerationTickLabelCount = 14;
    private const double XAxisTickLabelWidth = 18d;
    private const double YAxisTickLabelHeight = 12d;

    private string _ioAddress = $"{NetworkAddressDefaults.ResolveDefaultAdvertisedHost()}:12050";
    private string _ioGatewayName = "io-gateway";
    private string _clientName = "nbn.basics.ui";
    private string _bindHost = NetworkAddressDefaults.DefaultBindHost;
    private string _portText = "12094";
    private string _advertiseHost = NetworkAddressDefaults.ResolveDefaultAdvertisedHost();
    private string _advertisePortText = string.Empty;
    private string _requestTimeoutSecondsText = "120";
    private string _optionalSettingsAddress = string.Empty;
    private string _optionalSettingsActorName = "SettingsMonitor";
    private string _connectionStatus = "Disconnected";
    private string _capacityStatus = "No capacity snapshot fetched.";
    private string _capacitySummary = "Fetch capacity through IO to compute population bounds.";
    private string _validationSummary = "Configuration not yet validated.";
    private string _validationDetails = "No validation details available yet.";
    private string _bestBrainStatus = "No successful evaluation yet.";
    private string _bestBrainDetail = "The strongest evaluated brain appears here after the first successful generation.";
    private string _templateId = "basics-template-a";
    private string _templateDescription = "Seed all initial brains from one shared 2→2 template, allowing only bounded minor divergence.";
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
    private string _minimumPopulationOverrideText = string.Empty;
    private string _maximumPopulationOverrideText = string.Empty;
    private string _reproductionRunCountOverrideText = string.Empty;
    private string _maxConcurrentBrainsOverrideText = string.Empty;
    private string _recommendedInitialPopulationText = "—";
    private string _recommendedRunCountText = "—";
    private string _recommendedMaxConcurrentBrainsText = "—";
    private string _targetAccuracyText = "1.0";
    private string _targetFitnessText = "0.999";
    private bool _requireBothStopTargets = true;
    private string _maximumGenerationsText = string.Empty;
    private bool _adaptiveDiversityEnabled = true;
    private string _adaptiveDiversityStallGenerationWindowText = "4";
    private string _maxReadyWindowTicksText = "4";
    private string _sampleRepeatCountText = "1";
    private string _booleanLowInputValueText = "0.0";
    private string _booleanHighInputValueText = "1.0";
    private string _gtUniqueInputValuesText = "3";
    private string _multiplicationUniqueInputValuesText = "7";
    private string _multiplicationToleranceText = "0.03";
    private bool _multiplicationBehaviorOccupancyEnabled = true;
    private string _multiplicationBehaviorRampStartText = "0.35";
    private string _multiplicationBehaviorRampFullText = "0.50";
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
    private string _executionLogPath = "Run log: none yet.";
    private string _metricsStatus = "Connect to IO, fetch capacity, and start an implemented task to populate live metrics.";
    private string _metricsSecondaryStatus = "Population and resource summaries update when capacity is fetched or a run is active.";
    private string _winnerExportStatus = "No best-so-far brain retained.";
    private string _winnerExportDetail = "The strongest evaluated candidate is retained for export when a run finishes or reaches a stop target.";
    private string _workerCountText = "32";
    private string _workerBasePortText = "12041";
    private string _workerStoragePctText = "95";
    private string _workerLauncherStatus = "No workers launched from Basics UI.";
    private string _workerLauncherDetail = "Starts one local WorkerNode process on the configured port with the requested worker root count.";
    private string _initialBrainSeedStatus = "No initial brains uploaded.";
    private ArtifactRef? _winnerDefinitionArtifact;
    private ArtifactRef? _winnerSnapshotArtifact;
    private BasicsExecutionBestCandidateSummary? _winnerBestCandidate;
    private Guid _retainedWinnerBrainId;
    private Guid _liveWinnerBrainId;
    private string? _lastExportedWinnerArtifactSha;
    private TaskOption? _selectedTask;
    private StrengthSourceOption? _selectedStrengthSource;
    private OutputObservationModeOption? _selectedOutputObservationMode;
    private DiversityPresetOption? _selectedDiversityPreset;
    private bool _showBalancedAccuracyChart = true;
    private bool _showEdgeAccuracyChart;
    private bool _showInteriorAccuracyChart;

    public MainWindowViewModel(
        UiDispatcher dispatcher,
        IBasicsArtifactExportService artifactExportService,
        IBasicsBrainImportService brainImportService,
        IBasicsLocalWorkerProcessService workerProcessService)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _artifactExportService = artifactExportService ?? throw new ArgumentNullException(nameof(artifactExportService));
        _brainImportService = brainImportService ?? throw new ArgumentNullException(nameof(brainImportService));
        _workerProcessService = workerProcessService ?? throw new ArgumentNullException(nameof(workerProcessService));

        OutputObservationModes = new ObservableCollection<OutputObservationModeOption>(BuildOutputObservationModes());
        SelectedOutputObservationMode = OutputObservationModes.First(static option => option.Mode == BasicsOutputObservationMode.VectorPotential);

        Tasks = new ObservableCollection<TaskOption>(BuildTasks());
        DiversityPresets = new ObservableCollection<DiversityPresetOption>(BuildDiversityPresets());

        StrengthSources = new ObservableCollection<StrengthSourceOption>(BuildStrengthSources());
        SelectedStrengthSource = StrengthSources.First(static option => option.Value == Repro.StrengthSource.StrengthBaseOnly);

        ValidationErrors = new ObservableCollection<string>();
        MetricSummaries = new ObservableCollection<MetricSummaryItemViewModel>(BuildMetricSummaryItems());
        BestBrainSummaries = new ObservableCollection<MetricSummaryItemViewModel>(BuildBestBrainMetricSummaryItems());
        InitialBrainSeeds = new ObservableCollection<InitialBrainSeedItemViewModel>();

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
        FetchCapacityCommand = new AsyncRelayCommand(FetchCapacityAsync, CanBuildPlan);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
        ExportBestCommand = new AsyncRelayCommand(ExportWinningArtifactsAsync, CanExportBest);
        ApplySuggestedBoundsCommand = new RelayCommand(ApplySuggestedBounds, () => _lastPlan is not null);
        StartWorkersCommand = new AsyncRelayCommand(StartWorkersAsync, CanStartWorkers);
        StopWorkersCommand = new AsyncRelayCommand(StopWorkersAsync, CanStopWorkers);
        AddInitialBrainsCommand = new AsyncRelayCommand(AddInitialBrainsAsync);
        ClearInitialBrainsCommand = new RelayCommand(ClearInitialBrains, () => InitialBrainSeeds.Count > 0);

        SelectedDiversityPreset = DiversityPresets.First(static option => option.Value == BasicsDiversityPreset.Medium);
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

    public ObservableCollection<DiversityPresetOption> DiversityPresets { get; }

    public ObservableCollection<StrengthSourceOption> StrengthSources { get; }

    public ObservableCollection<OutputObservationModeOption> OutputObservationModes { get; }

    public ObservableCollection<string> ValidationErrors { get; }

    public ObservableCollection<MetricSummaryItemViewModel> MetricSummaries { get; }

    public ObservableCollection<MetricSummaryItemViewModel> BestBrainSummaries { get; }

    public ObservableCollection<InitialBrainSeedItemViewModel> InitialBrainSeeds { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand FetchCapacityCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ExportBestCommand { get; }

    public RelayCommand ApplySuggestedBoundsCommand { get; }

    public AsyncRelayCommand StartWorkersCommand { get; }

    public AsyncRelayCommand StopWorkersCommand { get; }

    public AsyncRelayCommand AddInitialBrainsCommand { get; }

    public RelayCommand ClearInitialBrainsCommand { get; }

    public string ExecutionLogPath
    {
        get => _executionLogPath;
        private set => SetProperty(ref _executionLogPath, value);
    }

    public string IoAddress
    {
        get => _ioAddress;
        set => SetProperty(ref _ioAddress, value);
    }

    public string DefaultIoAddressHint => $"{NetworkAddressDefaults.ResolveDefaultAdvertisedHost()}:12050";

    public string DefaultAdvertiseHostHint => NetworkAddressDefaults.ResolveDefaultAdvertisedHost();

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

    public string WorkerCountText
    {
        get => _workerCountText;
        set => SetProperty(ref _workerCountText, value);
    }

    public string WorkerBasePortText
    {
        get => _workerBasePortText;
        set => SetProperty(ref _workerBasePortText, value);
    }

    public string WorkerStoragePctText
    {
        get => _workerStoragePctText;
        set => SetProperty(ref _workerStoragePctText, value);
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

    public string ValidationDetails
    {
        get => _validationDetails;
        private set => SetProperty(ref _validationDetails, value);
    }

    public string BestBrainStatus
    {
        get => _bestBrainStatus;
        private set => SetProperty(ref _bestBrainStatus, value);
    }

    public string BestBrainDetail
    {
        get => _bestBrainDetail;
        private set => SetProperty(ref _bestBrainDetail, value);
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
            RaiseTaskSettingsBindings();
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

    public string MinimumPopulationOverrideText
    {
        get => _minimumPopulationOverrideText;
        set => SetProperty(ref _minimumPopulationOverrideText, value);
    }

    public string MaximumPopulationOverrideText
    {
        get => _maximumPopulationOverrideText;
        set => SetProperty(ref _maximumPopulationOverrideText, value);
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

    public bool RequireBothStopTargets
    {
        get => _requireBothStopTargets;
        set => SetProperty(ref _requireBothStopTargets, value);
    }

    public string MaximumGenerationsText
    {
        get => _maximumGenerationsText;
        set => SetProperty(ref _maximumGenerationsText, value);
    }

    public bool AdaptiveDiversityEnabled
    {
        get => _adaptiveDiversityEnabled;
        set => SetProperty(ref _adaptiveDiversityEnabled, value);
    }

    public string AdaptiveDiversityStallGenerationWindowText
    {
        get => _adaptiveDiversityStallGenerationWindowText;
        set => SetProperty(ref _adaptiveDiversityStallGenerationWindowText, value);
    }

    public string MaxReadyWindowTicksText
    {
        get => _maxReadyWindowTicksText;
        set => SetProperty(ref _maxReadyWindowTicksText, value);
    }

    public string SampleRepeatCountText
    {
        get => _sampleRepeatCountText;
        set => SetProperty(ref _sampleRepeatCountText, value);
    }

    public string BooleanLowInputValueText
    {
        get => _booleanLowInputValueText;
        set
        {
            if (SetProperty(ref _booleanLowInputValueText, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public string BooleanHighInputValueText
    {
        get => _booleanHighInputValueText;
        set
        {
            if (SetProperty(ref _booleanHighInputValueText, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public string GtUniqueInputValuesText
    {
        get => _gtUniqueInputValuesText;
        set
        {
            if (SetProperty(ref _gtUniqueInputValuesText, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public string MultiplicationUniqueInputValuesText
    {
        get => _multiplicationUniqueInputValuesText;
        set
        {
            if (SetProperty(ref _multiplicationUniqueInputValuesText, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public string MultiplicationToleranceText
    {
        get => _multiplicationToleranceText;
        set
        {
            if (SetProperty(ref _multiplicationToleranceText, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public bool MultiplicationBehaviorOccupancyEnabled
    {
        get => _multiplicationBehaviorOccupancyEnabled;
        set
        {
            if (SetProperty(ref _multiplicationBehaviorOccupancyEnabled, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public string MultiplicationBehaviorRampStartText
    {
        get => _multiplicationBehaviorRampStartText;
        set
        {
            if (SetProperty(ref _multiplicationBehaviorRampStartText, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public string MultiplicationBehaviorRampFullText
    {
        get => _multiplicationBehaviorRampFullText;
        set
        {
            if (SetProperty(ref _multiplicationBehaviorRampFullText, value))
            {
                RaiseTaskSettingsBindings();
            }
        }
    }

    public bool ShowTaskSettingsCard => SelectedTask is not null;

    public bool ShowBooleanTaskSettings
        => SelectedTask?.TaskId is "and" or "or" or "xor";

    public bool ShowGtTaskSettings => SelectedTask?.TaskId == "gt";

    public bool ShowMultiplicationTaskSettings => SelectedTask?.TaskId == "multiplication";

    public bool ShowUnavailableTaskSettingsMessage
        => ShowTaskSettingsCard
           && !ShowBooleanTaskSettings
           && !ShowGtTaskSettings
           && !ShowMultiplicationTaskSettings;

    public string TaskSettingsDetail
    {
        get
        {
            if (ShowBooleanTaskSettings)
            {
                return "Evaluates the four canonical truth-table combinations using the selected low/high input values. Output[0] is the task value and output[1] is the shared ready bit.";
            }

            if (ShowGtTaskSettings)
            {
                var count = TryParsePositiveInt(GtUniqueInputValuesText, fallbackValue: 3);
                return $"Evaluates {count * count} ordered comparisons over an evenly spaced {count}x{count} grid in [0,1]. Output[0] is the task value and output[1] is the shared ready bit.";
            }

            if (ShowMultiplicationTaskSettings)
            {
                var count = TryParsePositiveInt(MultiplicationUniqueInputValuesText, fallbackValue: 7);
                var interiorAxisCount = Math.Max(0, count - 2);
                var interiorCount = interiorAxisCount * interiorAxisCount;
                var edgeCount = count <= 2 ? count * count : Math.Min((count * count) - interiorCount, Math.Max(4, interiorCount));
                var behaviorDetail = MultiplicationBehaviorOccupancyEnabled
                    ? $" Behavior occupancy pressure ramps from balanced {MultiplicationBehaviorRampStartText} to {MultiplicationBehaviorRampFullText}."
                    : " Behavior occupancy pressure is disabled.";
                return $"Evaluates {interiorCount + edgeCount} stratified multiplication samples from an evenly spaced {count}x{count} grid in [0,1], keeping all interior points and a deterministic boundary subset so edge cases do not dominate. Output[0] is the task value and output[1] is the shared ready bit.{behaviorDetail}";
            }

            return "This task does not expose custom evaluation settings yet.";
        }
    }

    public StrengthSourceOption? SelectedStrengthSource
    {
        get => _selectedStrengthSource;
        set => SetProperty(ref _selectedStrengthSource, value);
    }

    public DiversityPresetOption? SelectedDiversityPreset
    {
        get => _selectedDiversityPreset;
        set
        {
            if (!SetProperty(ref _selectedDiversityPreset, value))
            {
                return;
            }

            if (!_suppressValidationRefresh)
            {
                ApplyDiversityPreset(value);
            }
        }
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

    public bool IsWorkerLauncherBusy
    {
        get => _isWorkerLauncherBusy;
        private set => SetProperty(ref _isWorkerLauncherBusy, value);
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

    public string WorkerLauncherStatus
    {
        get => _workerLauncherStatus;
        private set => SetProperty(ref _workerLauncherStatus, value);
    }

    public string WorkerLauncherDetail
    {
        get => _workerLauncherDetail;
        private set => SetProperty(ref _workerLauncherDetail, value);
    }

    public string InitialBrainSeedStatus
    {
        get => _initialBrainSeedStatus;
        private set => SetProperty(ref _initialBrainSeedStatus, value);
    }

    public bool ShowAccuracyMetricToggles
        => string.Equals(SelectedTask?.TaskId ?? _lastPlan?.SelectedTask.TaskId, "multiplication", StringComparison.OrdinalIgnoreCase)
           || _balancedAccuracyHistory.Count > 0
           || _bestBalancedAccuracyHistory.Count > 0
           || _edgeAccuracyHistory.Count > 0
           || _interiorAccuracyHistory.Count > 0;

    public bool ShowBalancedAccuracyChart
    {
        get => _showBalancedAccuracyChart;
        set
        {
            if (SetProperty(ref _showBalancedAccuracyChart, value))
            {
                UpdateChartBindings();
            }
        }
    }

    public bool ShowEdgeAccuracyChart
    {
        get => _showEdgeAccuracyChart;
        set
        {
            if (SetProperty(ref _showEdgeAccuracyChart, value))
            {
                UpdateChartBindings();
            }
        }
    }

    public bool ShowInteriorAccuracyChart
    {
        get => _showInteriorAccuracyChart;
        set
        {
            if (SetProperty(ref _showInteriorAccuracyChart, value))
            {
                UpdateChartBindings();
            }
        }
    }

    public bool HasAccuracyChartData => ResolveAccuracyChartGenerationCount() > 0;

    public bool HasFitnessChartData
        => _fitnessHistory.Count > 0
           || _bestFitnessHistory.Count > 0
           || _behaviorOccupancyHistory.Count > 0
           || _behaviorPressureHistory.Count > 0;

    public bool ShowAccuracyEmptyState => !HasAccuracyChartData;

    public bool ShowFitnessEmptyState => !HasFitnessChartData;

    public IReadOnlyList<float> AccuracyChartValues
        => UsePartitionedAccuracyChart
            ? (ShowBalancedAccuracyChart ? _balancedAccuracyHistory : Array.Empty<float>())
            : _accuracyHistory;

    public IReadOnlyList<float> BestAccuracyChartValues
        => UsePartitionedAccuracyChart
            ? (ShowBalancedAccuracyChart ? BuildBestSoFarHistory(_bestBalancedAccuracyHistory) : Array.Empty<float>())
            : BuildBestSoFarHistory(_bestAccuracyHistory);

    public IReadOnlyList<float> AccuracyTertiaryChartValues
        => UsePartitionedAccuracyChart && ShowEdgeAccuracyChart
            ? _edgeAccuracyHistory
            : Array.Empty<float>();

    public IReadOnlyList<float> AccuracyQuaternaryChartValues
        => UsePartitionedAccuracyChart && ShowInteriorAccuracyChart
            ? _interiorAccuracyHistory
            : Array.Empty<float>();

    public IReadOnlyList<float> FitnessChartValues => _fitnessHistory;

    public IReadOnlyList<float> BestFitnessChartValues => BuildBestSoFarHistory(_bestFitnessHistory);

    public IReadOnlyList<float> FitnessTertiaryChartValues => _behaviorOccupancyHistory;

    public IReadOnlyList<float> FitnessQuaternaryChartValues => _behaviorPressureHistory;

    public IReadOnlyList<Point> AccuracyChartPoints => BuildChartPoints(AccuracyChartValues);

    public IReadOnlyList<Point> BestAccuracyChartPoints => BuildChartPoints(BestAccuracyChartValues);

    public IReadOnlyList<Point> FitnessChartPoints => BuildChartPoints(_fitnessHistory);

    public IReadOnlyList<Point> BestFitnessChartPoints => BuildChartPoints(BuildBestSoFarHistory(_bestFitnessHistory));

    public IReadOnlyList<ChartAxisTickItem> AccuracyGenerationTicks
        => BuildGenerationTicks(
            AccuracyChartValues,
            BestAccuracyChartValues,
            AccuracyTertiaryChartValues,
            AccuracyQuaternaryChartValues);

    public IReadOnlyList<ChartAxisTickItem> FitnessGenerationTicks
        => BuildGenerationTicks(
            _fitnessHistory,
            _bestFitnessHistory,
            _behaviorOccupancyHistory,
            _behaviorPressureHistory);

    public IReadOnlyList<ChartAxisValueTickItem> NormalizedValueAxisTicks { get; } = BuildNormalizedValueAxisTicks();

    public bool ShowAccuracyStartGenerationTick => ResolveAccuracyChartGenerationCount() > 0;

    public bool ShowAccuracyMidGenerationTick
        => HasCenteredGenerationTick(ResolveAccuracyChartGenerationCount());

    public bool ShowAccuracyEndGenerationTick => ResolveAccuracyChartGenerationCount() > 1;

    public string AccuracyStartGenerationTickLabel
        => ShowAccuracyStartGenerationTick ? "1" : string.Empty;

    public string AccuracyMidGenerationTickLabel
        => ShowAccuracyMidGenerationTick
            ? FormatGenerationTickLabel(ResolveMidpointGeneration(ResolveAccuracyChartGenerationCount()))
            : string.Empty;

    public string AccuracyEndGenerationTickLabel
        => ShowAccuracyEndGenerationTick
            ? FormatGenerationTickLabel(ResolveAccuracyChartGenerationCount())
            : string.Empty;

    public bool ShowFitnessStartGenerationTick => ResolveChartGenerationCount(_fitnessHistory, _bestFitnessHistory) > 0;

    public bool ShowFitnessMidGenerationTick
        => HasCenteredGenerationTick(ResolveChartGenerationCount(_fitnessHistory, _bestFitnessHistory));

    public bool ShowFitnessEndGenerationTick => ResolveChartGenerationCount(_fitnessHistory, _bestFitnessHistory) > 1;

    public string FitnessStartGenerationTickLabel
        => ShowFitnessStartGenerationTick ? "1" : string.Empty;

    public string FitnessMidGenerationTickLabel
        => ShowFitnessMidGenerationTick
            ? FormatGenerationTickLabel(ResolveMidpointGeneration(ResolveChartGenerationCount(_fitnessHistory, _bestFitnessHistory)))
            : string.Empty;

    public string FitnessEndGenerationTickLabel
        => ShowFitnessEndGenerationTick
            ? FormatGenerationTickLabel(ResolveChartGenerationCount(_fitnessHistory, _bestFitnessHistory))
            : string.Empty;

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
            _runtimeClientOptions = options;
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

    private async Task StartWorkersAsync()
    {
        if (!TryBuildLocalWorkerLaunchRequest(out var request, out var failureMessage))
        {
            WorkerLauncherStatus = "Worker launch blocked.";
            WorkerLauncherDetail = failureMessage;
            RaiseCommandStates();
            return;
        }

        IsWorkerLauncherBusy = true;
        WorkerLauncherStatus = "Starting workers...";
        WorkerLauncherDetail = $"Launching {request.WorkerCount} local worker root(s) on a shared port starting at {request.BasePort}.";
        RaiseCommandStates();

        try
        {
            var result = await _workerProcessService.StartWorkersAsync(request).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                WorkerLauncherStatus = result.StatusText;
                WorkerLauncherDetail = result.DetailText;
                RaiseCommandStates();
            });

            if (result.StartedCount > 0)
            {
                _ = RefreshCapacityAfterWorkerTopologyChangeAsync();
            }
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                WorkerLauncherStatus = "Worker launch failed.";
                WorkerLauncherDetail = ex.GetBaseException().Message;
                RaiseCommandStates();
            });
        }
        finally
        {
            _dispatcher.Post(() =>
            {
                IsWorkerLauncherBusy = false;
                RaiseCommandStates();
            });
        }
    }

    private async Task StopWorkersAsync()
    {
        IsWorkerLauncherBusy = true;
        WorkerLauncherStatus = "Stopping workers...";
        WorkerLauncherDetail = "Stopping all worker processes launched by Basics UI in this session.";
        RaiseCommandStates();

        try
        {
            var result = await _workerProcessService.StopLaunchedWorkersAsync().ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                WorkerLauncherStatus = result.StatusText;
                WorkerLauncherDetail = result.DetailText;
                RaiseCommandStates();
            });

            _ = RefreshCapacityAfterWorkerTopologyChangeAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                WorkerLauncherStatus = "Worker stop failed.";
                WorkerLauncherDetail = ex.GetBaseException().Message;
                RaiseCommandStates();
            });
        }
        finally
        {
            _dispatcher.Post(() =>
            {
                IsWorkerLauncherBusy = false;
                RaiseCommandStates();
            });
        }
    }

    private async Task RefreshCapacityAfterWorkerTopologyChangeAsync()
    {
        if (_runtimeClient is null || IsExecutionRunning)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await FetchCapacityAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort capacity refresh only.
        }
    }

    private async Task AddInitialBrainsAsync()
    {
        try
        {
            var imported = await _brainImportService.ImportAsync().ConfigureAwait(false);
            if (imported.Count == 0)
            {
                return;
            }

            _dispatcher.Post(() =>
            {
                foreach (var file in imported)
                {
                    var analysis = BasicsDefinitionAnalyzer.Analyze(file.DefinitionBytes);
                    if (!analysis.Geometry.IsValid)
                    {
                        InitialBrainSeedStatus = $"Skipped {file.DisplayName}: expected {BasicsIoGeometry.InputWidth}->{BasicsIoGeometry.OutputWidth} geometry.";
                        continue;
                    }

                    UpsertInitialBrainSeed(file, analysis);
                }

                UpdateInitialBrainSeedStatus();
                RefreshValidationState();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => InitialBrainSeedStatus = $"Initial brain import failed: {ex.GetBaseException().Message}");
        }
    }

    private void ClearInitialBrains()
    {
        InitialBrainSeeds.Clear();
        UpdateInitialBrainSeedStatus();
        RaiseCommandStates();
        RefreshValidationState();
    }

    private void UpsertInitialBrainSeed(BasicsImportedBrainFile file, BasicsDefinitionAnalysis analysis)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(file.DefinitionBytes)).ToLowerInvariant();
        if (InitialBrainSeeds.Any(seed => string.Equals(seed.ContentHash, contentHash, StringComparison.Ordinal)))
        {
            InitialBrainSeedStatus = $"{file.DisplayName} is already loaded as an initial brain.";
            return;
        }

        InitialBrainSeedItemViewModel? item = null;
        item = new InitialBrainSeedItemViewModel(
            displayName: file.DisplayName,
            localPath: file.LocalPath,
            definitionBytes: file.DefinitionBytes,
            snapshotLocalPath: file.SnapshotLocalPath,
            snapshotBytes: file.SnapshotBytes,
            contentHash: contentHash,
            complexity: analysis.Complexity,
            duplicateForReproduction: true,
            removeCommand: new RelayCommand(
                () =>
                {
                    if (item is null)
                    {
                        return;
                    }

                    InitialBrainSeeds.Remove(item);
                    UpdateInitialBrainSeedStatus();
                    RaiseCommandStates();
                    RefreshValidationState();
                }));
        InitialBrainSeeds.Add(item);
        EnsureSeedBoundsCover(analysis.Complexity);
        RaiseCommandStates();
    }

    private void EnsureSeedBoundsCover(BasicsDefinitionComplexitySummary complexity)
    {
        _suppressValidationRefresh = true;
        try
        {
            MinActiveInternalRegionCountText = ExpandMinimum(MinActiveInternalRegionCountText, complexity.ActiveInternalRegionCount);
            MaxActiveInternalRegionCountText = ExpandMaximum(MaxActiveInternalRegionCountText, complexity.ActiveInternalRegionCount);
            MinInternalNeuronCountText = ExpandMinimum(MinInternalNeuronCountText, complexity.InternalNeuronCount);
            MaxInternalNeuronCountText = ExpandMaximum(MaxInternalNeuronCountText, complexity.InternalNeuronCount);
            MinAxonCountText = ExpandMinimum(MinAxonCountText, complexity.AxonCount);
            MaxAxonCountText = ExpandMaximum(MaxAxonCountText, complexity.AxonCount);
        }
        finally
        {
            _suppressValidationRefresh = false;
        }
    }

    private static string ExpandMinimum(string currentText, int requiredValue)
    {
        var current = string.IsNullOrWhiteSpace(currentText)
            ? (int?)null
            : int.TryParse(currentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        var next = current.HasValue ? Math.Min(current.Value, requiredValue) : requiredValue;
        return next.ToString(CultureInfo.InvariantCulture);
    }

    private static string ExpandMaximum(string currentText, int requiredValue)
    {
        var current = string.IsNullOrWhiteSpace(currentText)
            ? (int?)null
            : int.TryParse(currentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        var next = current.HasValue ? Math.Max(current.Value, requiredValue) : requiredValue;
        return next.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateInitialBrainSeedStatus()
    {
        InitialBrainSeedStatus = InitialBrainSeeds.Count == 0
            ? "No initial brains uploaded."
            : $"{InitialBrainSeeds.Count} unique initial brain(s) loaded. Uploaded brains replace the built template for initial seeding.";
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
        var taskSettings = profile.TaskSettings ?? new BasicsTaskSettings();
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
            MinimumPopulationOverrideText = FormatOptionalInt(profile.Sizing.MinimumPopulationCount);
            MaximumPopulationOverrideText = FormatOptionalInt(profile.Sizing.MaximumPopulationCount);
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
            TargetAccuracyText = profile.StopCriteria.TargetAccuracy.ToString("0.0##", CultureInfo.InvariantCulture);
            TargetFitnessText = profile.StopCriteria.TargetFitness.ToString("0.0##", CultureInfo.InvariantCulture);
            RequireBothStopTargets = profile.StopCriteria.RequireBothTargets;
            MaximumGenerationsText = FormatOptionalInt(profile.StopCriteria.MaximumGenerations);
            AdaptiveDiversityEnabled = profile.AdaptiveDiversity.Enabled;
            AdaptiveDiversityStallGenerationWindowText = profile.AdaptiveDiversity.StallGenerationWindow.ToString(CultureInfo.InvariantCulture);
            MaxReadyWindowTicksText = profile.OutputSamplingPolicy.MaxReadyWindowTicks.ToString(CultureInfo.InvariantCulture);
            SampleRepeatCountText = profile.OutputSamplingPolicy.SampleRepeatCount.ToString(CultureInfo.InvariantCulture);
            BooleanLowInputValueText = taskSettings.BooleanTruthTable.LowInputValue.ToString("0.0##", CultureInfo.InvariantCulture);
            BooleanHighInputValueText = taskSettings.BooleanTruthTable.HighInputValue.ToString("0.0##", CultureInfo.InvariantCulture);
            GtUniqueInputValuesText = taskSettings.Gt.UniqueInputValueCount.ToString(CultureInfo.InvariantCulture);
            MultiplicationUniqueInputValuesText = taskSettings.Multiplication.UniqueInputValueCount.ToString(CultureInfo.InvariantCulture);
            MultiplicationToleranceText = taskSettings.Multiplication.AccuracyTolerance.ToString("0.0##", CultureInfo.InvariantCulture);
            MultiplicationBehaviorOccupancyEnabled = taskSettings.Multiplication.BehaviorOccupancyEnabled;
            MultiplicationBehaviorRampStartText = taskSettings.Multiplication.BehaviorStageGateStart.ToString("0.0##", CultureInfo.InvariantCulture);
            MultiplicationBehaviorRampFullText = taskSettings.Multiplication.BehaviorStageGateFull.ToString("0.0##", CultureInfo.InvariantCulture);
            SelectedDiversityPreset = DiversityPresets.FirstOrDefault(option => option.Value == profile.DiversityPreset)
                ?? SelectedDiversityPreset;

            SelectedOutputObservationMode = OutputObservationModes.FirstOrDefault(option => option.Mode == profile.OutputObservationMode)
                ?? SelectedOutputObservationMode;
        }
        finally
        {
            _suppressValidationRefresh = false;
        }

        RefreshValidationState();
    }

    private void ApplyDiversityPreset(DiversityPresetOption? preset)
    {
        if (preset is null)
        {
            return;
        }

        var variation = BasicsDiversityTuning.CreateVariationBand(preset.Value);
        var scheduling = BasicsDiversityTuning.CreateScheduling(preset.Value);
        _suppressValidationRefresh = true;
        try
        {
            MaxInternalNeuronDeltaText = variation.MaxInternalNeuronDelta.ToString(CultureInfo.InvariantCulture);
            MaxAxonDeltaText = variation.MaxAxonDelta.ToString(CultureInfo.InvariantCulture);
            MaxStrengthCodeDeltaText = variation.MaxStrengthCodeDelta.ToString(CultureInfo.InvariantCulture);
            MaxParameterCodeDeltaText = variation.MaxParameterCodeDelta.ToString(CultureInfo.InvariantCulture);
            AllowFunctionMutation = variation.AllowFunctionMutation;
            AllowAxonReroute = variation.AllowAxonReroute;
            AllowRegionSetChange = variation.AllowRegionSetChange;

            FitnessWeightText = scheduling.ParentSelection.FitnessWeight.ToString("0.##", CultureInfo.InvariantCulture);
            DiversityWeightText = scheduling.ParentSelection.DiversityWeight.ToString("0.##", CultureInfo.InvariantCulture);
            SpeciesBalanceWeightText = scheduling.ParentSelection.SpeciesBalanceWeight.ToString("0.##", CultureInfo.InvariantCulture);
            EliteFractionText = scheduling.ParentSelection.EliteFraction.ToString("0.##", CultureInfo.InvariantCulture);
            ExplorationFractionText = scheduling.ParentSelection.ExplorationFraction.ToString("0.##", CultureInfo.InvariantCulture);
            MaxParentsPerSpeciesText = scheduling.ParentSelection.MaxParentsPerSpecies.ToString(CultureInfo.InvariantCulture);
            MinRunsPerPairText = scheduling.RunAllocation.MinRunsPerPair.ToString(CultureInfo.InvariantCulture);
            MaxRunsPerPairText = scheduling.RunAllocation.MaxRunsPerPair.ToString(CultureInfo.InvariantCulture);
            FitnessExponentText = scheduling.RunAllocation.FitnessExponent.ToString("0.##", CultureInfo.InvariantCulture);
            DiversityBoostText = scheduling.RunAllocation.DiversityBoost.ToString("0.##", CultureInfo.InvariantCulture);
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

            SetExecutionPhase(
                "Starting...",
                $"Preparing a fresh {options.SelectedTask.DisplayName} run from the current capacity and seed settings.");
            await StopExecutionAsync().ConfigureAwait(false);

            if (!TaskPluginRegistry.TryCreate(options.SelectedTask.TaskId, options.TaskSettings, out var plugin))
            {
                _dispatcher.Post(() =>
                {
                    ExecutionStatus = "Start blocked.";
                    ExecutionDetail = $"{options.SelectedTask.DisplayName} is not implemented yet.";
                });
                return;
            }

            SetExecutionPhase(
                "Starting...",
                "Verifying the direct IO session for this run.");
            if (!await EnsureRuntimeClientReadyForRunAsync(runtimeOptions).ConfigureAwait(false))
            {
                return;
            }

            SetExecutionPhase(
                "Starting...",
                $"Building the {options.SelectedTask.DisplayName} execution plan and sizing the initial population.");
            var planner = new BasicsEnvironmentPlanner(_runtimeClient);
            var plan = await planner.BuildPlanAsync(options).ConfigureAwait(false);
            var planTrace = BasicsTraceability.BuildPlanTrace(plan);
            var buildTrace = BuildCurrentBuildTrace();
            _dispatcher.Post(() => ApplyPlan(plan));
            _lastPlanTrace = planTrace;
            _lastBuildTrace = buildTrace;

            _dispatcher.Post(() => ClearWinnerState(clearArtifacts: true));

            _executionRunLog?.Dispose();
            try
            {
                _executionRunLog = BasicsExecutionRunLog.Create(plan, planTrace, buildTrace, IoAddress);
                _executionRunLog.AppendRunStarted(plan);
            }
            catch
            {
                _executionRunLog = null;
            }

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
                    ExecutionDetail = $"Launching {plan.SelectedTask.DisplayName} with template family {plan.SeedTemplate.TemplateId}. Diversity {FormatDiversityPreset(plan.DiversityPreset)} with adaptive stall boost {(plan.AdaptiveDiversity.Enabled ? "on" : "off")}. Stop target: {FormatStopCriteria(plan.StopCriteria)}, generation limit {FormatGenerationLimit(plan.StopCriteria.MaximumGenerations)}.";
                    ExecutionLogPath = _executionRunLog is null
                        ? "Run log: unavailable."
                        : $"Run log: {_executionRunLog.Path}";
                    RaiseCommandStates();
                });

            try
            {
                await session.RunAsync(
                        plan,
                        plugin,
                        snapshot =>
                        {
                            _executionRunLog?.AppendSnapshot(snapshot);
                            _dispatcher.Post(() => ApplyExecutionSnapshot(snapshot, plan.Capacity.RecommendedMaxConcurrentBrains));
                        },
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
                _executionRunLog?.Dispose();
                _executionRunLog = null;
                _dispatcher.Post(() =>
                {
                    IsExecutionRunning = false;
                    RaiseCommandStates();
                });
            }
        }
        catch (Exception ex)
        {
            _executionRunLog?.Dispose();
            _executionRunLog = null;
            _dispatcher.Post(() =>
            {
                ExecutionStatus = "Execution failed.";
                ExecutionDetail = ex.GetBaseException().Message;
            });
        }
    }

    private void SetExecutionPhase(string status, string detail)
    {
        _dispatcher.Post(() =>
        {
            ExecutionStatus = status;
            ExecutionDetail = detail;
        });
    }

    private async Task<bool> RestartRuntimeClientForRunAsync(BasicsRuntimeClientOptions runtimeOptions)
    {
        await DisposeRuntimeClientAsync().ConfigureAwait(false);

        Exception? lastFailure = null;
        foreach (var candidate in BuildRunScopedRuntimeClientCandidates(runtimeOptions))
        {
            try
            {
                var runtimeClient = await BasicsRuntimeClient.StartAsync(candidate).ConfigureAwait(false);
                var ack = await runtimeClient.ConnectAsync(ClientName.Trim()).ConfigureAwait(false);
                if (ack is null)
                {
                    lastFailure = new InvalidOperationException("ConnectAck was not received.");
                    await runtimeClient.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                _runtimeClient = runtimeClient;
                _runtimeClientOptions = candidate;
                _dispatcher.Post(() =>
                {
                    ConnectionStatus = $"Connected to {IoAddress} as {ack.ServerName}";
                    if (candidate.Port != runtimeOptions.Port)
                    {
                        PortText = candidate.Port.ToString(CultureInfo.InvariantCulture);
                    }

                    RaiseCommandStates();
                });
                return true;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
        }

        _dispatcher.Post(() =>
        {
            ConnectionStatus = $"IO reconnect failed: {lastFailure?.GetBaseException().Message ?? "unknown"}";
            ExecutionStatus = "Start blocked.";
            ExecutionDetail = "Could not reconnect the runtime client for a fresh run.";
            RaiseCommandStates();
        });
        return false;
    }

    private async Task<bool> EnsureRuntimeClientReadyForRunAsync(BasicsRuntimeClientOptions runtimeOptions)
    {
        if (_runtimeClient is not null
            && _runtimeClientOptions == runtimeOptions
            && await ProbeRuntimeClientAsync(_runtimeClient).ConfigureAwait(false))
        {
            _dispatcher.Post(RaiseCommandStates);
            return true;
        }

        SetExecutionPhase(
            "Starting...",
            _runtimeClient is null
                ? "Opening a direct IO session for this run."
                : _runtimeClientOptions == runtimeOptions
                    ? "Current IO session failed a direct probe; reconnecting."
                    : "Connection settings changed since Connect; opening a matching IO session for this run.");
        return await RestartRuntimeClientForRunAsync(runtimeOptions).ConfigureAwait(false);
    }

    private static async Task<bool> ProbeRuntimeClientAsync(IBasicsRuntimeClient runtimeClient)
    {
        try
        {
            return await runtimeClient.GetPlacementWorkerInventoryAsync().ConfigureAwait(false) is not null;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<BasicsRuntimeClientOptions> BuildRunScopedRuntimeClientCandidates(BasicsRuntimeClientOptions runtimeOptions)
    {
        yield return runtimeOptions;

        if (runtimeOptions.AdvertisePort.HasValue)
        {
            yield break;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            yield return runtimeOptions with
            {
                Port = ReserveLoopbackPort(runtimeOptions.BindHost)
            };
        }
    }

    private static int ReserveLoopbackPort(string bindHost)
    {
        IPAddress address;
        if (string.IsNullOrWhiteSpace(bindHost)
            || string.Equals(bindHost.Trim(), "0.0.0.0", StringComparison.Ordinal)
            || string.Equals(bindHost.Trim(), "::", StringComparison.Ordinal))
        {
            address = IPAddress.Loopback;
        }
        else if (!IPAddress.TryParse(bindHost.Trim(), out address!))
        {
            address = IPAddress.Loopback;
        }

        using var listener = new TcpListener(address, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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
        MetricsSecondaryStatus = $"Population {plan.Capacity.RecommendedInitialPopulationCount}, concurrent {plan.Capacity.RecommendedMaxConcurrentBrains}, base run count {plan.Capacity.RecommendedReproductionRunCount}, output mode {FormatOutputObservationMode(plan.OutputObservationMode)}, diversity {FormatDiversityPreset(plan.DiversityPreset)}, stop target {FormatStopCriteria(plan.StopCriteria)}, generation limit {FormatGenerationLimit(plan.StopCriteria.MaximumGenerations)}.";
        if (!IsExecutionRunning)
        {
            ExecutionStatus = "Ready to start.";
            ExecutionDetail = TaskPluginRegistry.TryGet(plan.SelectedTask.TaskId, out _)
                ? $"Template {plan.SeedTemplate.TemplateId} will be published automatically if no artifact ref is supplied. Output mode: {FormatOutputObservationMode(plan.OutputObservationMode)}. Diversity preset: {FormatDiversityPreset(plan.DiversityPreset)} with adaptive stall boost {(plan.AdaptiveDiversity.Enabled ? "on" : "off")}."
                : $"{plan.SelectedTask.DisplayName} cannot start until its plugin issue is implemented.";
        }

        UpdateMetricSummary(BasicsMetricId.PopulationCount, plan.Capacity.RecommendedInitialPopulationCount.ToString(CultureInfo.InvariantCulture), "Recommended initial population bound.");
        UpdateMetricSummary(BasicsMetricId.ActiveBrainCount, plan.Capacity.RecommendedMaxConcurrentBrains.ToString(CultureInfo.InvariantCulture), "Recommended max concurrent brains.");
        UpdateMetricSummary(BasicsMetricId.ReproductionRunsObserved, plan.Capacity.RecommendedReproductionRunCount.ToString(CultureInfo.InvariantCulture), "Suggested base reproduction run count before per-pair min/max shaping.");
        UpdateMetricSummary(BasicsMetricId.SpeciesCount, plan.SeedTemplate.ExpectSingleBootstrapSpecies ? "1 seed family" : "multiple", "Bootstrap template-family expectation.");
        UpdateMetricSummary(BasicsMetricId.CapacityUtilization, plan.Capacity.CapacityScore.ToString("0.###", CultureInfo.InvariantCulture), plan.Capacity.Source.ToString());
        UpdateMetricSummary(BasicsMetricId.OffspringBestFitness, "—", "No offspring evaluations yet.");
        UpdateMetricSummary(BasicsMetricId.LatestBatchDuration, "—", "Instrumentation appears after the first evaluated batch.");
        UpdateMetricSummary(BasicsMetricId.LatestSetupDuration, "—", "Instrumentation appears after the first evaluated batch.");
        UpdateMetricSummary(BasicsMetricId.LatestObservationDuration, "—", "Instrumentation appears after the first evaluated batch.");
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
        ValidationDetails = errors.Count == 0
            ? "No validation issues."
            : string.Join(global::System.Environment.NewLine, errors);
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

    private bool CanExportBest() => HasArtifactRef(_winnerDefinitionArtifact);

    private bool CanStartWorkers() => !IsWorkerLauncherBusy;

    private bool CanStopWorkers() => !IsWorkerLauncherBusy && _workerProcessService.LaunchedWorkerCount > 0;

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        FetchCapacityCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ExportBestCommand.RaiseCanExecuteChanged();
        ApplySuggestedBoundsCommand.RaiseCanExecuteChanged();
        StartWorkersCommand.RaiseCanExecuteChanged();
        StopWorkersCommand.RaiseCanExecuteChanged();
        AddInitialBrainsCommand.RaiseCanExecuteChanged();
        ClearInitialBrainsCommand.RaiseCanExecuteChanged();
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

    private bool TryBuildLocalWorkerLaunchRequest(
        out BasicsLocalWorkerLaunchRequest request,
        out string failureMessage)
    {
        request = default!;
        failureMessage = string.Empty;

        if (!int.TryParse(WorkerCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workerCount) || workerCount <= 0)
        {
            failureMessage = "Worker count must be a positive integer.";
            return false;
        }

        if (!int.TryParse(WorkerBasePortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var basePort) || basePort <= 0 || basePort > 65535)
        {
            failureMessage = "Worker port must be an integer between 1 and 65535.";
            return false;
        }

        if (!int.TryParse(WorkerStoragePctText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var storagePercent) || storagePercent < 0 || storagePercent > 100)
        {
            failureMessage = "Worker storage % must be an integer between 0 and 100.";
            return false;
        }

        var settingsName = string.IsNullOrWhiteSpace(OptionalSettingsActorName)
            ? "SettingsMonitor"
            : OptionalSettingsActorName.Trim();
        var settingsHost = "127.0.0.1";
        var settingsPort = 12010;
        if (!string.IsNullOrWhiteSpace(OptionalSettingsAddress))
        {
            if (!TryParseHostPort(OptionalSettingsAddress, out settingsHost, out settingsPort))
            {
                failureMessage = "Settings address must be empty or use host:port.";
                return false;
            }
        }

        if (!NetworkAddressDefaults.IsLoopbackHost(settingsHost))
        {
            failureMessage = "Basics UI worker launch currently supports only local loopback SettingsMonitor endpoints. Use Workbench for remote/shared worker orchestration.";
            return false;
        }

        var bindHost = string.IsNullOrWhiteSpace(BindHost) ? NetworkAddressDefaults.DefaultBindHost : BindHost.Trim();
        if (!NetworkAddressDefaults.IsLocalHost(bindHost))
        {
            failureMessage = "Basics UI worker launch requires a local bind host or all-interfaces bind.";
            return false;
        }

        var advertiseHost = NetworkAddressDefaults.ResolveAdvertisedHost(
            bindHost,
            string.IsNullOrWhiteSpace(AdvertiseHost) ? null : AdvertiseHost.Trim());
        if (!NetworkAddressDefaults.IsLocalHost(advertiseHost))
        {
            failureMessage = "Basics UI worker launch requires an advertised host that resolves to this machine.";
            return false;
        }

        request = new BasicsLocalWorkerLaunchRequest(
            WorkerCount: workerCount,
            BasePort: basePort,
            StoragePercent: storagePercent,
            BindHost: bindHost,
            AdvertiseHost: advertiseHost,
            SettingsHost: settingsHost,
            SettingsPort: settingsPort,
            SettingsName: settingsName);
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
            MinimumPopulationCount = ParseOptionalInt(MinimumPopulationOverrideText, "Minimum population override", errors),
            MaximumPopulationCount = ParseOptionalInt(MaximumPopulationOverrideText, "Maximum population override", errors),
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
            TargetFitness = ParseRequiredFloat(TargetFitnessText, "Stop fitness target", errors),
            RequireBothTargets = RequireBothStopTargets,
            MaximumGenerations = ParseOptionalInt(MaximumGenerationsText, "Maximum generations", errors)
        };
        var adaptiveDiversity = new BasicsAdaptiveDiversityOptions
        {
            Enabled = AdaptiveDiversityEnabled,
            StallGenerationWindow = ParseRequiredInt(
                AdaptiveDiversityStallGenerationWindowText,
                "Adaptive stall window",
                errors)
        };
        var outputSamplingPolicy = new BasicsOutputSamplingPolicy
        {
            MaxReadyWindowTicks = ParseRequiredInt(MaxReadyWindowTicksText, "Ready window ticks", errors),
            SampleRepeatCount = ParseRequiredInt(SampleRepeatCountText, "Sample repeat count", errors)
        };
        var taskSettings = new BasicsTaskSettings
        {
            BooleanTruthTable = new BasicsBinaryTruthTableTaskSettings
            {
                LowInputValue = ParseRequiredFloat(BooleanLowInputValueText, "Boolean low input value", errors),
                HighInputValue = ParseRequiredFloat(BooleanHighInputValueText, "Boolean high input value", errors)
            },
            Gt = new BasicsScalarGridTaskSettings
            {
                UniqueInputValueCount = ParseRequiredInt(GtUniqueInputValuesText, "GT unique input values", errors)
            },
            Multiplication = new BasicsMultiplicationTaskSettings
            {
                UniqueInputValueCount = ParseRequiredInt(MultiplicationUniqueInputValuesText, "Multiplication unique input values", errors),
                AccuracyTolerance = ParseRequiredFloat(MultiplicationToleranceText, "Multiplication accuracy tolerance", errors),
                BehaviorOccupancyEnabled = MultiplicationBehaviorOccupancyEnabled,
                BehaviorStageGateStart = ParseRequiredFloat(MultiplicationBehaviorRampStartText, "Multiplication behavior ramp start", errors),
                BehaviorStageGateFull = ParseRequiredFloat(MultiplicationBehaviorRampFullText, "Multiplication behavior ramp full score", errors)
            }
        };
        var diversityPreset = SelectedDiversityPreset?.Value ?? BasicsDiversityPreset.Medium;
        var reproductionConfig = ReproductionSettings.CreateDefaultConfig();
        reproductionConfig.ProtectIoRegionNeuronCounts = true;
        BasicsDiversityTuning.ApplyPresetToConfig(reproductionConfig, diversityPreset);

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
            OutputSamplingPolicy = outputSamplingPolicy,
            DiversityPreset = diversityPreset,
            AdaptiveDiversity = adaptiveDiversity,
            Reproduction = new BasicsReproductionPolicy
            {
                Config = reproductionConfig,
                StrengthSource = SelectedStrengthSource?.Value ?? Repro.StrengthSource.StrengthBaseOnly
            },
            Scheduling = scheduling,
            StopCriteria = stopCriteria,
            TaskSettings = taskSettings,
            InitialBrainSeeds = InitialBrainSeeds.Select(seed => new BasicsInitialBrainSeed(
                seed.DisplayName,
                seed.DefinitionBytes.ToArray(),
                seed.DuplicateForReproduction,
                seed.Complexity)
            {
                ContentHash = seed.ContentHash,
                SnapshotBytes = seed.SnapshotBytes?.ToArray()
            }).ToArray()
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
                ExecutionDetail = "Releasing the retained best-so-far brain and export artifacts.";
                RaiseCommandStates();
            });

            await ReleaseRetainedWinnerAsync("basics_ui_release_winner").ConfigureAwait(false);
            _dispatcher.Post(() => ClearWinnerState(clearArtifacts: true));
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

        if (snapshot.LatestBatchTiming is not null)
        {
            detailLines.Add(BuildBatchTimingStatus(snapshot.LatestBatchTiming));
        }

        if (snapshot.LatestGenerationTiming is not null)
        {
            detailLines.Add(BuildGenerationTimingStatus(snapshot.LatestGenerationTiming));
        }

        var resolvedDetail = string.Join(" ", detailLines);
        ExecutionStatus = snapshot.StatusText;
        ExecutionDetail = resolvedDetail;
        MetricsStatus = snapshot.StatusText;
        LastPlanSummary = $"Generation {snapshot.Generation} · population {snapshot.PopulationCount} · species {snapshot.SpeciesCount}.";

        ReplaceHistory(_accuracyHistory, snapshot.OffspringAccuracyHistory);
        ReplaceHistory(_bestAccuracyHistory, snapshot.AccuracyHistory);
        ReplaceHistory(_balancedAccuracyHistory, snapshot.OffspringBalancedAccuracyHistory);
        ReplaceHistory(_bestBalancedAccuracyHistory, snapshot.BalancedAccuracyHistory);
        ReplaceHistory(_edgeAccuracyHistory, snapshot.OffspringEdgeAccuracyHistory);
        ReplaceHistory(_interiorAccuracyHistory, snapshot.OffspringInteriorAccuracyHistory);
        ReplaceHistory(_fitnessHistory, snapshot.OffspringFitnessHistory);
        ReplaceHistory(_bestFitnessHistory, snapshot.BestFitnessHistory);
        ReplaceHistory(_behaviorOccupancyHistory, snapshot.BehaviorOccupancyHistory);
        ReplaceHistory(_behaviorPressureHistory, snapshot.BehaviorPressureHistory);
        UpdateChartBindings();

        var displayedAccuracy = ResolveLatestMetric(snapshot.OffspringAccuracyHistory, snapshot.OffspringBestAccuracy);
        var displayedBestAccuracy = ResolvePeakMetric(snapshot.AccuracyHistory, snapshot.BestAccuracy);
        var displayedBalancedAccuracy = ResolveLatestMetric(_balancedAccuracyHistory, 0f);
        var displayedEdgeAccuracy = ResolveLatestMetric(_edgeAccuracyHistory, 0f);
        var displayedInteriorAccuracy = ResolveLatestMetric(_interiorAccuracyHistory, 0f);
        var displayedOffspringFitness = ResolveLatestMetric(snapshot.OffspringFitnessHistory, snapshot.OffspringBestFitness);
        var displayedBestFitness = ResolvePeakMetric(snapshot.BestFitnessHistory, snapshot.BestFitness);
        var bestBrainBalancedAccuracy = ResolveBestCandidateReadyWeightedBalancedAccuracy(snapshot.BestCandidate);
        var bestBrainEdgeAccuracy = ResolveBestCandidateAccuracyMetric(snapshot.BestCandidate, "edge_tolerance_accuracy");
        var bestBrainInteriorAccuracy = ResolveBestCandidateAccuracyMetric(snapshot.BestCandidate, "interior_tolerance_accuracy");
        var hasPartitionedAccuracy = _balancedAccuracyHistory.Count > 0
            || _edgeAccuracyHistory.Count > 0
            || _interiorAccuracyHistory.Count > 0
            || bestBrainBalancedAccuracy.HasValue
            || bestBrainEdgeAccuracy.HasValue
            || bestBrainInteriorAccuracy.HasValue;

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

        if (snapshot.BestCandidate is null)
        {
            BestBrainStatus = "No successful evaluation yet.";
            BestBrainDetail = "The strongest evaluated brain appears here after the first successful generation.";
        }
        else
        {
            var shortSha = snapshot.BestCandidate.ArtifactSha256[..Math.Min(12, snapshot.BestCandidate.ArtifactSha256.Length)];
            BestBrainStatus = $"Best-so-far artifact {shortSha}";
            var accuracyDetail = hasPartitionedAccuracy && bestBrainBalancedAccuracy.HasValue
                ? $"raw accuracy {snapshot.BestCandidate.Accuracy:0.###}, ready-weighted balanced {bestBrainBalancedAccuracy.Value:0.###}, fitness {snapshot.BestCandidate.Fitness:0.###}."
                : $"accuracy {snapshot.BestCandidate.Accuracy:0.###}, fitness {snapshot.BestCandidate.Fitness:0.###}.";
            var originDetail = snapshot.BestCandidate.BootstrapOrigin is null
                ? string.Empty
                : $" Origin {DescribeBootstrapOrigin(snapshot.BestCandidate.BootstrapOrigin)}.";
            BestBrainDetail = snapshot.BestCandidate.Generation > 0
                ? $"Generation {snapshot.BestCandidate.Generation}, species {snapshot.BestCandidate.SpeciesId}, {accuracyDetail}{originDetail}"
                : $"Species {snapshot.BestCandidate.SpeciesId}, {accuracyDetail}{originDetail}";
        }

        UpdateMetricSummary(
            BasicsMetricId.Accuracy,
            displayedAccuracy.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.Generation <= 0
                ? "No offspring evaluations yet."
                : $"Best raw tolerance accuracy among newly evaluated members in generation {snapshot.Generation}.");
        UpdateMetricSummary(
            BasicsMetricId.BestAccuracy,
            displayedBestAccuracy.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null
                ? "No successful evaluation yet."
                : $"Best raw accuracy across all successful evaluations so far.");
        UpdateMetricSummary(
            BasicsMetricId.BalancedAccuracy,
            hasPartitionedAccuracy
                ? displayedBalancedAccuracy.ToString("0.###", CultureInfo.InvariantCulture)
                : "—",
            hasPartitionedAccuracy
                ? $"Balanced offspring multiplication accuracy for generation {snapshot.Generation}; edge samples are down-weighted relative to interior samples."
                : "Balanced multiplication accuracy appears when the active task publishes partitioned accuracy metrics.");
        UpdateMetricSummary(
            BasicsMetricId.EdgeAccuracy,
            hasPartitionedAccuracy
                ? displayedEdgeAccuracy.ToString("0.###", CultureInfo.InvariantCulture)
                : "—",
            hasPartitionedAccuracy
                ? $"Offspring accuracy on multiplication edge samples where one input is 0 or 1 in generation {snapshot.Generation}."
                : "Edge-specific multiplication accuracy appears when the active task publishes partitioned accuracy metrics.");
        UpdateMetricSummary(
            BasicsMetricId.InteriorAccuracy,
            hasPartitionedAccuracy
                ? displayedInteriorAccuracy.ToString("0.###", CultureInfo.InvariantCulture)
                : "—",
            hasPartitionedAccuracy
                ? $"Offspring accuracy on multiplication interior samples where both inputs are strictly between 0 and 1 in generation {snapshot.Generation}."
                : "Interior-specific multiplication accuracy appears when the active task publishes partitioned accuracy metrics.");
        UpdateMetricSummary(
            BasicsMetricId.OffspringBestFitness,
            displayedOffspringFitness.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.Generation <= 0
                ? "No offspring evaluations yet."
                : $"Best fitness among newly evaluated members in generation {snapshot.Generation}.");
        UpdateMetricSummary(
            BasicsMetricId.BestFitness,
            displayedBestFitness.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null
                ? "No successful evaluation yet."
                : $"Artifact {snapshot.BestCandidate.ArtifactSha256[..Math.Min(12, snapshot.BestCandidate.ArtifactSha256.Length)]}...");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateFitness,
            snapshot.BestCandidate is null
                ? "—"
                : snapshot.BestCandidate.Fitness.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null
                ? "No successful evaluation yet."
                : $"Fitness for the current best-so-far brain from generation {snapshot.BestCandidate.Generation}.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateBalancedAccuracy,
            bestBrainBalancedAccuracy.HasValue
                ? bestBrainBalancedAccuracy.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : "—",
            bestBrainBalancedAccuracy.HasValue
                ? "Ready-weighted balanced accuracy for the current best-so-far brain. This multiplies balanced multiplication accuracy by ready confidence so the card matches record-selection semantics."
                : "Ready-weighted balanced best-brain accuracy appears when the active task publishes partitioned accuracy metrics.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateEdgeAccuracy,
            bestBrainEdgeAccuracy.HasValue
                ? bestBrainEdgeAccuracy.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : "—",
            bestBrainEdgeAccuracy.HasValue
                ? "Accuracy for the current best-so-far brain on multiplication edge samples."
                : "Edge-specific best-brain accuracy appears when the active task publishes partitioned accuracy metrics.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateInteriorAccuracy,
            bestBrainInteriorAccuracy.HasValue
                ? bestBrainInteriorAccuracy.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : "—",
            bestBrainInteriorAccuracy.HasValue
                ? "Accuracy for the current best-so-far brain on multiplication interior samples."
                : "Interior-specific best-brain accuracy appears when the active task publishes partitioned accuracy metrics.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateGeneration,
            snapshot.BestCandidate is null || snapshot.BestCandidate.Generation <= 0
                ? "—"
                : snapshot.BestCandidate.Generation.ToString(CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null || snapshot.BestCandidate.Generation <= 0
                ? "No successful evaluation yet."
                : $"Generation where the current best-so-far brain was evaluated.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateAverageReadyTicks,
            snapshot.BestCandidate?.AverageReadyTickCount is float averageReadyTicks
                ? FormatReadyTickCount(averageReadyTicks)
                : "—",
            snapshot.BestCandidate?.AverageReadyTickCount is float
                ? "Average ready-bit arrival tick across canonical samples for the current best-so-far brain."
                : "No ready-bit timing data yet for the current best-so-far brain.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateInternalNeuronCount,
            snapshot.BestCandidate?.Complexity is { } complexity
                ? complexity.InternalNeuronCount.ToString(CultureInfo.InvariantCulture)
                : "—",
            snapshot.BestCandidate?.Complexity is not null
                ? "Internal neuron count for the current best-so-far brain."
                : "No complexity data yet for the current best-so-far brain.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateAxonCount,
            snapshot.BestCandidate?.Complexity is { } bestComplexity
                ? bestComplexity.AxonCount.ToString(CultureInfo.InvariantCulture)
                : "—",
            snapshot.BestCandidate?.Complexity is not null
                ? "Axon count for the current best-so-far brain."
                : "No complexity data yet for the current best-so-far brain.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateReadyTickRange,
            snapshot.BestCandidate?.MinReadyTickCount is float minReadyTick
                && snapshot.BestCandidate?.MedianReadyTickCount is float medianReadyTick
                && snapshot.BestCandidate?.MaxReadyTickCount is float maxReadyTick
                ? $"{FormatReadyTickCount(minReadyTick)} / {FormatReadyTickCount(medianReadyTick)} / {FormatReadyTickCount(maxReadyTick)}"
                : "—",
            snapshot.BestCandidate?.MinReadyTickCount is float
                ? "Current best-so-far brain ready ticks shown as min / median / max across canonical samples."
                : "No ready-bit timing data yet for the current best-so-far brain.");
        UpdateMetricSummary(
            BasicsMetricId.BestCandidateReadyTickStdDev,
            snapshot.BestCandidate?.ReadyTickStdDev is float stdDevReadyTick
                ? FormatReadyTickCount(stdDevReadyTick)
                : "—",
            snapshot.BestCandidate?.ReadyTickStdDev is float
                ? "Standard deviation of ready-bit arrival ticks for the current best-so-far brain across canonical samples."
                : "No ready-bit dispersion data yet for the current best-so-far brain.");
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
        UpdateMetricSummary(
            BasicsMetricId.LatestBatchDuration,
            snapshot.LatestBatchTiming is null
                ? "—"
                : FormatRuntimeSeconds(snapshot.LatestBatchTiming.BatchDurationSeconds),
            snapshot.LatestBatchTiming is null
                ? "Instrumentation appears after the first evaluated batch."
                : $"Batch {snapshot.LatestBatchTiming.BatchIndex}/{snapshot.LatestBatchTiming.BatchCount}: queue {FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageQueueWaitSeconds)}/brain, spawn {FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageSpawnRequestSeconds)}/brain, placement {FormatRuntimeSeconds(snapshot.LatestBatchTiming.AveragePlacementWaitSeconds)}/brain.");
        UpdateMetricSummary(
            BasicsMetricId.LatestSetupDuration,
            snapshot.LatestBatchTiming is null
                ? "—"
                : FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageSetupSeconds),
            snapshot.LatestBatchTiming is null
                ? "Instrumentation appears after the first evaluated batch."
                : "Average setup time per brain: brain-info/configure/subscribe before observation starts.");
        UpdateMetricSummary(
            BasicsMetricId.LatestObservationDuration,
            snapshot.LatestBatchTiming is null
                ? "—"
                : FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageObservationSeconds),
            snapshot.LatestBatchTiming is null
                ? "Instrumentation appears after the first evaluated batch."
                : BuildObservationMetricDetail(snapshot.LatestBatchTiming));

        if (CanUseBestCandidateForExport(snapshot.BestCandidate))
        {
            ApplyWinnerArtifacts(
                snapshot.BestCandidate!,
                retainWinner: snapshot.State is BasicsExecutionState.Succeeded or BasicsExecutionState.Failed or BasicsExecutionState.Stopped);
        }
        else if (snapshot.State is BasicsExecutionState.Failed or BasicsExecutionState.Stopped)
        {
            ClearWinnerState(clearArtifacts: false);
        }
    }

    private async Task ExportWinningArtifactsAsync()
    {
        if (!HasArtifactRef(_winnerDefinitionArtifact))
        {
            WinnerExportStatus = "No best-so-far definition available.";
            WinnerExportDetail = "Run at least one evaluated generation before exporting.";
            return;
        }

        try
        {
            var liveDefinitionArtifact = CanCaptureLiveWinnerDefinition()
                ? await TryExportLiveWinnerDefinitionAsync().ConfigureAwait(false)
                : null;
            var definitionArtifact = HasArtifactRef(liveDefinitionArtifact)
                ? liveDefinitionArtifact!.Clone()
                : _winnerDefinitionArtifact!.Clone();
            var effectiveBestCandidate = BuildEffectiveWinnerSummary(_winnerBestCandidate, definitionArtifact);
            var winnerArtifactSha = definitionArtifact.ToSha256Hex();
            var definitionPath = await _artifactExportService.ExportAsync(
                    definitionArtifact,
                    title: "Export best definition",
                    suggestedFileName: BuildSuggestedArtifactFileName("best", "nbn"))
                .ConfigureAwait(false);
            if (definitionPath is null)
            {
                _dispatcher.Post(() =>
                {
                    WinnerExportStatus = "Best-so-far export canceled.";
                    WinnerExportDetail = "The best-so-far .nbn artifact is still available for export.";
                    RaiseCommandStates();
                });
                return;
            }

            var snapshotEligible = HasArtifactRef(_winnerSnapshotArtifact) || CanCaptureLiveWinnerSnapshot();
            var snapshotAvailable = HasArtifactRef(_winnerSnapshotArtifact);
            ArtifactRef? snapshotArtifact = null;
            if (!snapshotAvailable && CanCaptureLiveWinnerSnapshot())
            {
                snapshotArtifact = await TryCaptureLiveWinnerSnapshotAsync(winnerArtifactSha).ConfigureAwait(false);
                snapshotAvailable = HasArtifactRef(snapshotArtifact);
            }

            string? snapshotPath = null;
            if (snapshotAvailable)
            {
                snapshotArtifact ??= _winnerSnapshotArtifact!.Clone();
                snapshotPath = await _artifactExportService.ExportAsync(
                        snapshotArtifact,
                        title: "Export best snapshot",
                        suggestedFileName: BuildSuggestedArtifactFileName("best-state", "nbs"))
                    .ConfigureAwait(false);
            }

            var provenanceWriteResult = await TryWriteWinnerTraceabilityAsync(
                    definitionPath,
                    snapshotPath,
                    effectiveBestCandidate,
                    definitionArtifact,
                    snapshotArtifact,
                    usedLiveDefinitionExport: HasArtifactRef(liveDefinitionArtifact),
                    usedLiveSnapshotCapture: HasArtifactRef(snapshotArtifact) && !HasArtifactRef(_winnerSnapshotArtifact))
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                if (HasArtifactRef(liveDefinitionArtifact))
                {
                    _winnerDefinitionArtifact = liveDefinitionArtifact!.Clone();
                    if (_winnerBestCandidate is not null)
                    {
                        _winnerBestCandidate = CloneBestCandidateSummary(effectiveBestCandidate);
                    }
                }

                var exportResult = BuildExportResult(
                    definitionPath,
                    snapshotPath,
                    provenanceWriteResult.SidecarPath,
                    provenanceWriteResult.Warning,
                    winnerArtifactSha,
                    snapshotEligible,
                    snapshotAvailable);
                WinnerExportStatus = exportResult.Status;
                WinnerExportDetail = exportResult.Detail;
                RaiseCommandStates();
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                WinnerExportStatus = "Best-so-far export failed.";
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

    private void ApplyWinnerArtifacts(BasicsExecutionBestCandidateSummary winner, bool retainWinner)
    {
        var currentWinnerSha = _winnerDefinitionArtifact?.ToSha256Hex();
        var nextWinnerSha = winner.DefinitionArtifact.ToSha256Hex();
        var sameWinner = string.Equals(currentWinnerSha, nextWinnerSha, StringComparison.OrdinalIgnoreCase);

        _winnerDefinitionArtifact = winner.DefinitionArtifact.Clone();
        _winnerBestCandidate = CloneBestCandidateSummary(winner);
        if (HasArtifactRef(winner.SnapshotArtifact))
        {
            _winnerSnapshotArtifact = winner.SnapshotArtifact!.Clone();
        }
        else if (!sameWinner)
        {
            _winnerSnapshotArtifact = null;
        }

        _liveWinnerBrainId = winner.ActiveBrainId ?? Guid.Empty;
        _retainedWinnerBrainId = retainWinner ? _liveWinnerBrainId : Guid.Empty;
        UpdateWinnerExportSummary();
        RaiseCommandStates();
    }

    private void ClearWinnerState(bool clearArtifacts)
    {
        _liveWinnerBrainId = Guid.Empty;
        _retainedWinnerBrainId = Guid.Empty;
        if (clearArtifacts)
        {
            _winnerDefinitionArtifact = null;
            _winnerSnapshotArtifact = null;
            _winnerBestCandidate = null;
            _lastExportedWinnerArtifactSha = null;
        }

        UpdateWinnerExportSummary();
        RaiseCommandStates();
    }

    private void UpdateWinnerExportSummary()
    {
        if (!HasArtifactRef(_winnerDefinitionArtifact))
        {
            WinnerExportStatus = "No best-so-far brain retained.";
            WinnerExportDetail = "The strongest evaluated candidate is retained for export when a run finishes or reaches a stop target.";
            return;
        }

        WinnerExportStatus = _retainedWinnerBrainId != Guid.Empty
            ? "Best-so-far brain retained for export."
            : IsExecutionRunning
                ? "Current best-so-far artifacts available."
                : "Best-so-far artifacts retained.";

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
            detailParts.Add(CanCaptureLiveWinnerSnapshot()
                ? "Snapshot .nbs can be captured from the current live best brain on export."
                : "No snapshot artifact is currently available for this candidate.");
        }

        if (_retainedWinnerBrainId != Guid.Empty)
        {
            detailParts.Add($"Live best brain {_retainedWinnerBrainId:N} stays active until the retained export is cleared by Stop, Disconnect, or the next run.");
        }
        else if (IsExecutionRunning)
        {
            detailParts.Add("These artifacts can change while the run is still active.");
        }

        if (_winnerBestCandidate?.BootstrapOrigin is { } bootstrapOrigin)
        {
            detailParts.Add($"Origin: {DescribeBootstrapOrigin(bootstrapOrigin)}.");
        }

        WinnerExportDetail = string.Join(' ', detailParts);
    }

    private bool CanCaptureLiveWinnerSnapshot()
        => _liveWinnerBrainId != Guid.Empty && _runtimeClient is not null;

    private bool CanCaptureLiveWinnerDefinition()
        => _liveWinnerBrainId != Guid.Empty && _runtimeClient is not null;

    private async Task<ArtifactRef?> TryExportLiveWinnerDefinitionAsync()
    {
        var runtimeClient = _runtimeClient;
        var liveWinnerBrainId = _liveWinnerBrainId;
        if (runtimeClient is null || liveWinnerBrainId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var ready = await runtimeClient.ExportBrainDefinitionAsync(
                    liveWinnerBrainId,
                    rebaseOverlays: true)
                .ConfigureAwait(false);
            return HasArtifactRef(ready?.BrainDef)
                ? ready!.BrainDef!.Clone()
                : null;
        }
        catch
        {
            // Live definition export is opportunistic during active or retained runs.
            return null;
        }
    }

    private async Task<ArtifactRef?> TryCaptureLiveWinnerSnapshotAsync(string expectedWinnerArtifactSha)
    {
        var runtimeClient = _runtimeClient;
        var liveWinnerBrainId = _liveWinnerBrainId;
        if (runtimeClient is null || liveWinnerBrainId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var ready = await runtimeClient.RequestSnapshotAsync(liveWinnerBrainId).ConfigureAwait(false);
            if (!HasArtifactRef(ready?.Snapshot))
            {
                return null;
            }

            var snapshotArtifact = ready!.Snapshot!.Clone();

            _dispatcher.Post(() =>
            {
                if (!HasArtifactRef(_winnerDefinitionArtifact)
                    || !string.Equals(_winnerDefinitionArtifact!.ToSha256Hex(), expectedWinnerArtifactSha, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _winnerSnapshotArtifact = snapshotArtifact.Clone();
                UpdateWinnerExportSummary();
                RaiseCommandStates();
            });
            return snapshotArtifact;
        }
        catch
        {
            // Snapshot capture is opportunistic during active runs.
            return null;
        }
    }

    private static BasicsExecutionBestCandidateSummary? BuildEffectiveWinnerSummary(
        BasicsExecutionBestCandidateSummary? winner,
        ArtifactRef definitionArtifact)
        => winner is null
            ? null
            : CloneBestCandidateSummary(winner with
            {
                DefinitionArtifact = definitionArtifact.Clone()
            });

    private async Task<(string? SidecarPath, string Warning)> TryWriteWinnerTraceabilityAsync(
        string definitionPath,
        string? snapshotPath,
        BasicsExecutionBestCandidateSummary? winner,
        ArtifactRef definitionArtifact,
        ArtifactRef? snapshotArtifact,
        bool usedLiveDefinitionExport,
        bool usedLiveSnapshotCapture)
    {
        try
        {
            var tracePath = $"{definitionPath}.trace.json";
            var planTrace = _lastPlanTrace
                ?? (_lastPlan is null ? null : BasicsTraceability.BuildPlanTrace(_lastPlan));
            var buildTrace = _lastBuildTrace ?? BuildCurrentBuildTrace();
            var traceRecord = new WinnerExportTraceabilityRecord(
                SchemaVersion: BasicsTraceability.SchemaVersion,
                ExportedAtUtc: DateTimeOffset.UtcNow,
                TaskId: _lastPlan?.SelectedTask.TaskId ?? SelectedTask?.TaskId ?? "unknown",
                TaskDisplayName: _lastPlan?.SelectedTask.DisplayName ?? SelectedTask?.DisplayName ?? "Unknown",
                IoAddress: IoAddress,
                RunLogPath: NormalizeRunLogPath(ExecutionLogPath),
                DefinitionPath: definitionPath,
                DefinitionArtifactSha256: definitionArtifact.ToSha256Hex(),
                SnapshotPath: snapshotPath,
                SnapshotArtifactSha256: HasArtifactRef(snapshotArtifact) ? snapshotArtifact!.ToSha256Hex() : null,
                UsedLiveDefinitionExport: usedLiveDefinitionExport,
                UsedLiveSnapshotCapture: usedLiveSnapshotCapture,
                Plan: planTrace,
                Build: buildTrace,
                BestCandidate: winner is null
                    ? null
                    : new WinnerExportBestCandidateTrace(
                        winner.ArtifactSha256,
                        winner.SpeciesId,
                        winner.Generation,
                        winner.Accuracy,
                        winner.Fitness,
                        new Dictionary<string, float>(winner.ScoreBreakdown, StringComparer.Ordinal),
                        winner.Diagnostics.ToArray(),
                        winner.BootstrapOrigin,
                        winner.AverageReadyTickCount,
                        winner.MinReadyTickCount,
                        winner.MedianReadyTickCount,
                        winner.MaxReadyTickCount,
                        winner.ReadyTickStdDev));
            var json = JsonSerializer.Serialize(traceRecord, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(tracePath, json).ConfigureAwait(false);
            return (tracePath, string.Empty);
        }
        catch (Exception ex)
        {
            return (null, $" Trace sidecar write failed: {ex.GetBaseException().Message}.");
        }
    }

    private (string Status, string Detail) BuildExportResult(
        string definitionPath,
        string? snapshotPath,
        string? traceabilityPath,
        string traceabilityWarning,
        string winnerArtifactSha,
        bool snapshotEligible,
        bool snapshotAvailable)
    {
        var warning = string.Empty;
        if (!string.IsNullOrWhiteSpace(_lastExportedWinnerArtifactSha)
            && !string.Equals(_lastExportedWinnerArtifactSha, winnerArtifactSha, StringComparison.OrdinalIgnoreCase))
        {
            warning = " Warning: the best-so-far brain changed since the previous export, so these files may not match the earlier export.";
        }

        _lastExportedWinnerArtifactSha = winnerArtifactSha;
        var traceabilityDetail = string.IsNullOrWhiteSpace(traceabilityPath)
            ? traceabilityWarning
            : $" Trace: {traceabilityPath}.{traceabilityWarning}";
        var definitionDetail = $"Definition: {definitionPath}";

        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            return (
                Status: "Best-so-far definition + snapshot exported.",
                Detail: $"{definitionDetail} Snapshot: {snapshotPath}.{traceabilityDetail}{warning}".Trim());
        }

        if (snapshotEligible && snapshotAvailable)
        {
            return (
                Status: "Best-so-far definition exported; snapshot export canceled.",
                Detail: $"{definitionDetail} The .nbs export was canceled.{traceabilityDetail}{warning}".Trim());
        }

        if (snapshotEligible)
        {
            return (
                Status: "Best-so-far definition exported.",
                Detail: $"{definitionDetail} Snapshot state was not available for this export.{traceabilityDetail}{warning}".Trim());
        }

        return (
            Status: "Best-so-far definition exported.",
            Detail: $"{definitionDetail}{traceabilityDetail}{warning}".Trim());
    }

    private async Task DisposeRuntimeClientAsync()
    {
        if (_runtimeClient is null)
        {
            return;
        }

        var current = _runtimeClient;
        _runtimeClient = null;
        _runtimeClientOptions = null;
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

    private static BasicsExecutionBestCandidateSummary? CloneBestCandidateSummary(BasicsExecutionBestCandidateSummary? candidate)
        => candidate is null
            ? null
            : candidate with
            {
                DefinitionArtifact = candidate.DefinitionArtifact.Clone(),
                SnapshotArtifact = candidate.SnapshotArtifact?.Clone(),
                ScoreBreakdown = new Dictionary<string, float>(candidate.ScoreBreakdown, StringComparer.Ordinal),
                Diagnostics = candidate.Diagnostics.ToArray()
            };

    private static string DescribeBootstrapOrigin(BasicsBootstrapOrigin origin)
    {
        var kindText = origin.Kind switch
        {
            BasicsBootstrapOriginKind.UploadedExactCopy => "uploaded seed exact copy",
            BasicsBootstrapOriginKind.UploadedVariation => "uploaded seed variation",
            BasicsBootstrapOriginKind.TemplateExactCopy => "template exact copy",
            BasicsBootstrapOriginKind.TemplateVariation => "template variation",
            _ => "bootstrap origin"
        };
        var detail = $"{kindText} '{origin.SourceDisplayName}'";
        if (!string.IsNullOrWhiteSpace(origin.SourceContentHash))
        {
            detail += $" ({origin.SourceContentHash[..Math.Min(12, origin.SourceContentHash.Length)]})";
        }

        if (origin.ExactCopyOrdinal is int exactCopyOrdinal)
        {
            detail += $" copy #{exactCopyOrdinal}";
        }

        return detail;
    }

    private static string? NormalizeRunLogPath(string runLogPath)
    {
        if (string.IsNullOrWhiteSpace(runLogPath))
        {
            return null;
        }

        const string prefix = "Run log:";
        var normalized = runLogPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? runLogPath[prefix.Length..].Trim()
            : runLogPath.Trim();
        return string.Equals(normalized, "none yet.", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    private static BasicsBuildTraceRecord BuildCurrentBuildTrace()
        => BasicsTraceability.BuildBuildTrace(
            assemblies:
            [
                typeof(MainWindowViewModel).Assembly,
                typeof(BasicsEnvironmentPlan).Assembly,
                typeof(TaskPluginRegistry).Assembly
            ],
            repositories: ResolveTraceabilityRepositoryTargets());

    private static IReadOnlyList<BasicsTraceabilityRepositoryTarget> ResolveTraceabilityRepositoryTargets()
    {
        var targets = new List<BasicsTraceabilityRepositoryTarget>();
        var demosRepoRoot = ResolveBasicsRepoRoot();
        if (string.IsNullOrWhiteSpace(demosRepoRoot))
        {
            return targets;
        }

        targets.Add(new BasicsTraceabilityRepositoryTarget("NBNv2-demos", demosRepoRoot));
        var runtimeRepoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(demosRepoRoot, "..", "NBNv2"));
        if (File.Exists(System.IO.Path.Combine(runtimeRepoRoot, "NBNv2.sln")) || Directory.Exists(System.IO.Path.Combine(runtimeRepoRoot, ".git")))
        {
            targets.Add(new BasicsTraceabilityRepositoryTarget("NBNv2-local-sibling", runtimeRepoRoot));
        }

        return targets;
    }

    private static string? ResolveBasicsRepoRoot()
    {
        var assemblyDirectory = System.IO.Path.GetDirectoryName(typeof(MainWindowViewModel).Assembly.Location) ?? string.Empty;
        foreach (var start in new[] { assemblyDirectory, AppContext.BaseDirectory, global::System.Environment.CurrentDirectory })
        {
            var current = start;
            while (!string.IsNullOrWhiteSpace(current))
            {
                var nestedBasicsRoot = System.IO.Path.Combine(current, "Basics");
                if (File.Exists(System.IO.Path.Combine(nestedBasicsRoot, "Basics.sln")))
                {
                    return current;
                }

                if (File.Exists(System.IO.Path.Combine(current, "Basics.sln")))
                {
                    return Directory.GetParent(current)?.FullName ?? current;
                }

                current = Directory.GetParent(current)?.FullName ?? string.Empty;
            }
        }

        return null;
    }

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

    private static bool TryParseHostPort(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator <= 0 || separator >= trimmed.Length - 1)
        {
            return false;
        }

        host = trimmed[..separator].Trim();
        return host.Length > 0
               && int.TryParse(trimmed[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
               && port > 0;
    }

    private static string FormatOptionalInt(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatOptionalUInt(uint? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatReadyTickCount(float value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static float ResolveLatestMetric(IReadOnlyList<float> history, float fallbackValue)
        => history.Count == 0 ? fallbackValue : history[^1];

    private static float ResolvePeakMetric(IReadOnlyList<float> history, float fallbackValue)
        => history.Count == 0 ? fallbackValue : Math.Max(fallbackValue, history.Max());

    private static float? ResolveBestCandidateAccuracyMetric(BasicsExecutionBestCandidateSummary? bestCandidate, string key)
        => bestCandidate?.ScoreBreakdown.TryGetValue(key, out var value) == true
            ? Math.Clamp(value, 0f, 1f)
            : null;

    private static float? ResolveBestCandidateReadyWeightedBalancedAccuracy(BasicsExecutionBestCandidateSummary? bestCandidate)
    {
        var balanced = ResolveBestCandidateAccuracyMetric(bestCandidate, "balanced_tolerance_accuracy");
        if (!balanced.HasValue)
        {
            return null;
        }

        var readyConfidence = ResolveBestCandidateAccuracyMetric(bestCandidate, "ready_confidence") ?? 1f;
        return Math.Clamp(balanced.Value * readyConfidence, 0f, 1f);
    }

    private static bool CanUseBestCandidateForExport(BasicsExecutionBestCandidateSummary? bestCandidate)
        => bestCandidate is not null
           && bestCandidate.Diagnostics.Count == 0
           && HasArtifactRef(bestCandidate.DefinitionArtifact);

    private static string FormatGenerationLimit(int? maximumGenerations)
        => maximumGenerations.HasValue
            ? maximumGenerations.Value.ToString(CultureInfo.InvariantCulture)
            : "unlimited";

    private static int TryParsePositiveInt(string text, int fallbackValue)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallbackValue;

    private void RaiseTaskSettingsBindings()
    {
        OnPropertyChanged(nameof(ShowTaskSettingsCard));
        OnPropertyChanged(nameof(ShowBooleanTaskSettings));
        OnPropertyChanged(nameof(ShowGtTaskSettings));
        OnPropertyChanged(nameof(ShowMultiplicationTaskSettings));
        OnPropertyChanged(nameof(ShowUnavailableTaskSettingsMessage));
        OnPropertyChanged(nameof(ShowAccuracyMetricToggles));
        OnPropertyChanged(nameof(TaskSettingsDetail));
        UpdateChartBindings();
    }

    private void ResetCharts()
    {
        _accuracyHistory.Clear();
        _bestAccuracyHistory.Clear();
        _balancedAccuracyHistory.Clear();
        _bestBalancedAccuracyHistory.Clear();
        _edgeAccuracyHistory.Clear();
        _interiorAccuracyHistory.Clear();
        _fitnessHistory.Clear();
        _bestFitnessHistory.Clear();
        _behaviorOccupancyHistory.Clear();
        _behaviorPressureHistory.Clear();
        UpdateChartBindings();
    }

    private void ReplaceHistory(List<float> target, IReadOnlyList<float> source)
    {
        if (source.Count == 0)
        {
            return;
        }

        target.Clear();
        target.AddRange(source);
    }

    private void UpdateChartBindings()
    {
        OnPropertyChanged(nameof(HasAccuracyChartData));
        OnPropertyChanged(nameof(HasFitnessChartData));
        OnPropertyChanged(nameof(ShowAccuracyEmptyState));
        OnPropertyChanged(nameof(ShowFitnessEmptyState));
        OnPropertyChanged(nameof(AccuracyChartValues));
        OnPropertyChanged(nameof(BestAccuracyChartValues));
        OnPropertyChanged(nameof(AccuracyTertiaryChartValues));
        OnPropertyChanged(nameof(AccuracyQuaternaryChartValues));
        OnPropertyChanged(nameof(FitnessChartValues));
        OnPropertyChanged(nameof(BestFitnessChartValues));
        OnPropertyChanged(nameof(FitnessTertiaryChartValues));
        OnPropertyChanged(nameof(FitnessQuaternaryChartValues));
        OnPropertyChanged(nameof(AccuracyChartPoints));
        OnPropertyChanged(nameof(BestAccuracyChartPoints));
        OnPropertyChanged(nameof(FitnessChartPoints));
        OnPropertyChanged(nameof(BestFitnessChartPoints));
        OnPropertyChanged(nameof(AccuracyGenerationTicks));
        OnPropertyChanged(nameof(FitnessGenerationTicks));
        OnPropertyChanged(nameof(ShowAccuracyStartGenerationTick));
        OnPropertyChanged(nameof(ShowAccuracyMidGenerationTick));
        OnPropertyChanged(nameof(ShowAccuracyEndGenerationTick));
        OnPropertyChanged(nameof(AccuracyStartGenerationTickLabel));
        OnPropertyChanged(nameof(AccuracyMidGenerationTickLabel));
        OnPropertyChanged(nameof(AccuracyEndGenerationTickLabel));
        OnPropertyChanged(nameof(ShowFitnessStartGenerationTick));
        OnPropertyChanged(nameof(ShowFitnessMidGenerationTick));
        OnPropertyChanged(nameof(ShowFitnessEndGenerationTick));
        OnPropertyChanged(nameof(FitnessStartGenerationTickLabel));
        OnPropertyChanged(nameof(FitnessMidGenerationTickLabel));
        OnPropertyChanged(nameof(FitnessEndGenerationTickLabel));
    }

    private bool UsePartitionedAccuracyChart => ShowAccuracyMetricToggles;

    private int ResolveAccuracyChartGenerationCount()
        => ResolveChartGenerationCount(
            AccuracyChartValues,
            BestAccuracyChartValues,
            AccuracyTertiaryChartValues,
            AccuracyQuaternaryChartValues);

    private static IReadOnlyList<Point> BuildChartPoints(IReadOnlyList<float> history)
    {
        if (history.Count == 0)
        {
            return Array.Empty<Point>();
        }

        var plotWidth = ChartPlotWidth - (ChartStrokeInset * 2f);
        var plotHeight = ChartPlotHeight - (ChartStrokeInset * 2f);
        if (history.Count == 1)
        {
            var y = (ChartPlotHeight - ChartStrokeInset) - (Math.Clamp(history[0], 0f, 1f) * plotHeight);
            return new[] { new Point(ChartStrokeInset, y) };
        }

        var stepX = plotWidth / (history.Count - 1f);
        return history.Select((value, index) =>
        {
            var x = ChartStrokeInset + (index * stepX);
            var y = (ChartPlotHeight - ChartStrokeInset) - (Math.Clamp(value, 0f, 1f) * plotHeight);
            return new Point(x, y);
        }).ToArray();
    }

    private static IReadOnlyList<float> BuildBestSoFarHistory(IReadOnlyList<float> history)
    {
        if (history.Count == 0)
        {
            return Array.Empty<float>();
        }

        var bestSoFar = new float[history.Count];
        var runningBest = float.MinValue;
        for (var index = 0; index < history.Count; index++)
        {
            runningBest = Math.Max(runningBest, history[index]);
            bestSoFar[index] = runningBest;
        }

        return bestSoFar;
    }

    private static IReadOnlyList<ChartAxisTickItem> BuildGenerationTicks(params IReadOnlyList<float>[] histories)
    {
        var generationCount = ResolveChartGenerationCount(histories);
        if (generationCount == 0)
        {
            return Array.Empty<ChartAxisTickItem>();
        }

        var tickCount = Math.Min(MaxGenerationTickLabelCount, generationCount);
        var tickIndices = BuildTickIndices(generationCount, tickCount);
        var ticks = new ChartAxisTickItem[tickIndices.Count];
        var plotWidth = ChartPlotWidth - (ChartStrokeInset * 2f);
        var labelHalfWidth = XAxisTickLabelWidth / 2d;
        var maxLabelLeft = Math.Max(0d, ChartPlotWidth - XAxisTickLabelWidth);

        for (var index = 0; index < tickIndices.Count; index++)
        {
            var generationIndex = tickIndices[index];
            var x = generationCount == 1
                ? ChartStrokeInset
                : ChartStrokeInset + ((generationIndex * plotWidth) / (generationCount - 1d));
            var labelLeft = Math.Clamp(x - labelHalfWidth, 0d, maxLabelLeft);
            ticks[index] = new ChartAxisTickItem(
                X: x,
                LabelLeft: labelLeft,
                LabelText: FormatGenerationTickLabel(generationIndex + 1));
        }

        return ticks;
    }

    private static IReadOnlyList<ChartAxisValueTickItem> BuildNormalizedValueAxisTicks()
    {
        var values = new[] { 1.0f, 0.8f, 0.6f, 0.4f, 0.2f, 0.0f };
        var plotHeight = ChartPlotHeight - (ChartStrokeInset * 2f);
        var maxLabelTop = Math.Max(0d, ChartPlotHeight - YAxisTickLabelHeight);

        return values.Select(value =>
        {
            var y = (ChartPlotHeight - ChartStrokeInset) - (value * plotHeight);
            var labelTop = Math.Clamp(y - (YAxisTickLabelHeight / 2d), 0d, maxLabelTop);
            return new ChartAxisValueTickItem(
                Y: y,
                LabelTop: labelTop,
                LabelText: value.ToString("0.0", CultureInfo.InvariantCulture));
        }).ToArray();
    }

    private static IReadOnlyList<int> BuildTickIndices(int generationCount, int tickCount)
    {
        if (tickCount >= generationCount)
        {
            return Enumerable.Range(0, generationCount).ToArray();
        }

        var indices = new List<int>(tickCount);
        for (var slot = 0; slot < tickCount; slot++)
        {
            var index = (int)Math.Round(
                (slot * (generationCount - 1d)) / Math.Max(1d, tickCount - 1d),
                MidpointRounding.AwayFromZero);
            if (indices.Count == 0 || indices[^1] != index)
            {
                indices.Add(index);
            }
        }

        if (indices[^1] != generationCount - 1)
        {
            indices[^1] = generationCount - 1;
        }

        return indices;
    }

    private static int ResolveChartGenerationCount(params IReadOnlyList<float>[] histories)
        => histories.Length == 0
            ? 0
            : histories.Max(static history => history.Count);

    private static bool HasCenteredGenerationTick(int generationCount)
        => generationCount > 2 && generationCount % 2 == 1;

    private static int ResolveMidpointGeneration(int generationCount)
        => Math.Max(2, 1 + (generationCount / 2));

    private static string FormatGenerationTickLabel(int generation)
        => generation.ToString(CultureInfo.InvariantCulture);

    private void UpdateMetricSummary(BasicsMetricId metricId, string value, string detail)
    {
        var item = MetricSummaries.FirstOrDefault(entry => entry.MetricId == metricId)
                   ?? BestBrainSummaries.FirstOrDefault(entry => entry.MetricId == metricId);
        if (item is null)
        {
            return;
        }

        item.ValueText = value;
        item.DetailText = detail;
    }

    private static string BuildBatchTimingStatus(BasicsExecutionBatchTimingSummary timing)
    {
        var detail = $"Last batch {timing.BatchIndex}/{timing.BatchCount}: total {FormatRuntimeSeconds(timing.BatchDurationSeconds)}, queue {FormatRuntimeSeconds(timing.AverageQueueWaitSeconds)}/brain, spawn {FormatRuntimeSeconds(timing.AverageSpawnRequestSeconds)}/brain, placement {FormatRuntimeSeconds(timing.AveragePlacementWaitSeconds)}/brain, setup {FormatRuntimeSeconds(timing.AverageSetupSeconds)}/brain, {FormatObservationTiming(timing)}.";
        if (!string.IsNullOrWhiteSpace(timing.FailureSummary))
        {
            detail += $" Failures: {timing.FailureSummary}.";
        }

        return detail;
    }

    private static string BuildGenerationTimingStatus(BasicsExecutionGenerationTimingSummary timing)
    {
        var detail = $"Generation timing: total {FormatRuntimeSeconds(timing.TotalDurationSeconds)}, avg batch {FormatRuntimeSeconds(timing.AverageBatchDurationSeconds)}, placement {FormatRuntimeSeconds(timing.AveragePlacementWaitSeconds)}/brain, setup {FormatRuntimeSeconds(timing.AverageSetupSeconds)}/brain, {FormatObservationTiming(timing)}.";
        if (!string.IsNullOrWhiteSpace(timing.FailureSummary))
        {
            detail += $" Failures: {timing.FailureSummary}.";
        }

        return detail;
    }

    private static string FormatRuntimeSeconds(double seconds)
        => seconds >= 10d
            ? $"{seconds:0.0}s"
            : $"{seconds:0.00}s";

    private static string FormatRuntimeMilliseconds(double seconds)
        => $"{seconds * 1000d:0.#}ms";

    private static string BuildObservationMetricDetail(BasicsExecutionBatchTimingSummary timing)
        => $"Average observation wall time per brain, including neutral prime plus sample attempts; {timing.AverageObservationAttemptCount:0.#} sample attempts/brain, {FormatRuntimeMilliseconds(timing.AverageObservationSecondsPerAttempt)}/attempt including prime overhead. Breakdown/brain: pause {FormatRuntimeMilliseconds(timing.AverageObservationPauseSeconds)}, reset {FormatRuntimeMilliseconds(timing.AverageObservationResetSeconds)}, input {FormatRuntimeMilliseconds(timing.AverageObservationInputSeconds)}, resume {FormatRuntimeMilliseconds(timing.AverageObservationResumeSeconds)}, wait {FormatRuntimeMilliseconds(timing.AverageObservationWaitSeconds)}.";

    private static string FormatObservationTiming(BasicsExecutionBatchTimingSummary timing)
        => $"observe {FormatRuntimeSeconds(timing.AverageObservationSeconds)}/brain all-samples+prime ({timing.AverageObservationAttemptCount:0.#} sample attempts, {FormatRuntimeMilliseconds(timing.AverageObservationSecondsPerAttempt)}/attempt; pause {FormatRuntimeMilliseconds(timing.AverageObservationPauseSeconds)}, reset {FormatRuntimeMilliseconds(timing.AverageObservationResetSeconds)}, input {FormatRuntimeMilliseconds(timing.AverageObservationInputSeconds)}, resume {FormatRuntimeMilliseconds(timing.AverageObservationResumeSeconds)}, wait {FormatRuntimeMilliseconds(timing.AverageObservationWaitSeconds)})";

    private static string FormatObservationTiming(BasicsExecutionGenerationTimingSummary timing)
        => $"observe {FormatRuntimeSeconds(timing.AverageObservationSeconds)}/brain all-samples+prime ({timing.AverageObservationAttemptCount:0.#} sample attempts, {FormatRuntimeMilliseconds(timing.AverageObservationSecondsPerAttempt)}/attempt; pause {FormatRuntimeMilliseconds(timing.AverageObservationPauseSeconds)}, reset {FormatRuntimeMilliseconds(timing.AverageObservationResetSeconds)}, input {FormatRuntimeMilliseconds(timing.AverageObservationInputSeconds)}, resume {FormatRuntimeMilliseconds(timing.AverageObservationResumeSeconds)}, wait {FormatRuntimeMilliseconds(timing.AverageObservationWaitSeconds)})";

    private static IEnumerable<TaskOption> BuildTasks()
    {
        yield return CreateTaskOption("and", "AND", "Boolean AND over canonical 0/1 inputs and outputs.", "Plugin pending");
        yield return CreateTaskOption("or", "OR", "Boolean OR over canonical 0/1 inputs and outputs.", "Plugin pending");
        yield return CreateTaskOption("xor", "XOR", "Boolean XOR over canonical 0/1 inputs and outputs.", "Plugin pending");
        yield return CreateTaskOption("gt", "GT", "Boolean greater-than over canonical 0/1 or bounded scalar inputs.", "Plugin pending");
        yield return CreateTaskOption("multiplication", "Multiplication", "Bounded scalar multiplication over the shared 2->2 Basics geometry.", "Plugin pending");
        yield return CreateTaskOption("denoise", "Noisy in → clean out", "Denoising task over the shared 2->2 Basics geometry.", "Plugin pending");
        yield return CreateTaskOption("delay", "Delayed out", "Temporal delayed-output task over the shared 2->2 Basics geometry.", "Plugin pending");
        yield return CreateTaskOption("one-hot", "One-hot classifier", "Viability still pending for the shared 2->2 Basics geometry.", "Viability pending");
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
            "Reads the shared [value, ready] vector every tick from activation/potential and scores the first tick whose ready lane is high.");
        yield return new OutputObservationModeOption(
            BasicsOutputObservationMode.EventedOutput,
            "OutputEvent",
            "Uses OutputEvent on the ready lane as the scoring gate, with the paired value taken from the same tick's full vector sample.");
        yield return new OutputObservationModeOption(
            BasicsOutputObservationMode.VectorBuffer,
            "Continuous buffer",
            "Reads the shared [value, ready] vector every tick from persistent buffer values and scores the first tick whose ready lane is high.");
    }

    private static IEnumerable<DiversityPresetOption> BuildDiversityPresets()
    {
        yield return new DiversityPresetOption(BasicsDiversityPreset.Low, "Low", "Conservative reproduction and low exploration.");
        yield return new DiversityPresetOption(BasicsDiversityPreset.Medium, "Medium", "Balanced exploration and mutation pressure.");
        yield return new DiversityPresetOption(BasicsDiversityPreset.High, "High", "More structural churn and stronger diversity pressure.");
        yield return new DiversityPresetOption(BasicsDiversityPreset.Extreme, "Extreme", "Maximum exploration pressure for stubborn plateaus.");
    }

    private static IEnumerable<MetricSummaryItemViewModel> BuildMetricSummaryItems()
    {
        yield return new MetricSummaryItemViewModel(BasicsMetricId.Accuracy, "Offspring raw acc", "—", "No offspring evaluations yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BalancedAccuracy, "Offspring balanced", "—", "No partitioned multiplication accuracy yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.EdgeAccuracy, "Offspring edge", "—", "No partitioned multiplication accuracy yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.InteriorAccuracy, "Offspring interior", "—", "No partitioned multiplication accuracy yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.OffspringBestFitness, "Offspring fitness", "—", "No offspring evaluations yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.MeanFitness, "Mean fitness", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.PopulationCount, "Population", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ActiveBrainCount, "Active brains", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.SpeciesCount, "Species", "1 seed family", "Template-anchored bootstrap assumption.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ReproductionCalls, "Reproduction calls", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ReproductionRunsObserved, "Runs observed", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.CapacityUtilization, "Capacity score", "—", "Filled from IO capacity planning.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.LatestBatchDuration, "Last batch", "—", "Instrumentation appears after the first evaluated batch.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.LatestSetupDuration, "Setup/brain", "—", "Average brain-info/configure/subscribe/prime time per evaluated batch.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.LatestObservationDuration, "Observe all/brain", "—", "Average observation time per evaluated brain, including neutral prime plus all sample attempts.");
    }

    private static IEnumerable<MetricSummaryItemViewModel> BuildBestBrainMetricSummaryItems()
    {
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateBalancedAccuracy, "Ready bal", "—", "Ready-weighted balanced accuracy for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestAccuracy, "Raw acc", "—", "Best raw accuracy across all successful evaluations.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateEdgeAccuracy, "Edge", "—", "Edge-sample accuracy for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateInteriorAccuracy, "Interior", "—", "Interior-sample accuracy for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateFitness, "Fitness", "—", "Fitness for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateGeneration, "Gen", "—", "Generation where the current best-so-far brain was evaluated.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateAverageReadyTicks, "Avg ready", "—", "Average ready-bit arrival tick for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateReadyTickRange, "Ready ticks", "—", "Min / median / max ready-bit arrival ticks for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateReadyTickStdDev, "Ready stddev", "—", "Standard deviation of ready-bit arrival ticks for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateInternalNeuronCount, "Neurons", "—", "Internal neuron count for the current best-so-far brain.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestCandidateAxonCount, "Axons", "—", "Axon count for the current best-so-far brain.");
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

    private static string FormatDiversityPreset(BasicsDiversityPreset preset)
        => preset switch
        {
            BasicsDiversityPreset.Low => "low",
            BasicsDiversityPreset.High => "high",
            BasicsDiversityPreset.Extreme => "extreme",
            _ => "medium"
        };

    private static string FormatStopCriteria(BasicsExecutionStopCriteria stopCriteria)
        => stopCriteria.RequireBothTargets
            ? $"both accuracy >= {stopCriteria.TargetAccuracy:0.###} and fitness >= {stopCriteria.TargetFitness:0.###}"
            : $"either accuracy >= {stopCriteria.TargetAccuracy:0.###} or fitness >= {stopCriteria.TargetFitness:0.###}";

    private sealed class BasicsExecutionRunLog : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan RuntimeProcessMemorySampleInterval = TimeSpan.FromSeconds(15);
        private static readonly int NewLineByteCount = Encoding.UTF8.GetByteCount(global::System.Environment.NewLine);
        private StreamWriter _writer;
        private readonly object _gate = new();
        private readonly BasicsExecutionPlanTraceRecord _planTrace;
        private readonly BasicsBuildTraceRecord _buildTrace;
        private readonly string _ioAddress;
        private readonly string _root;
        private readonly string _taskSegment;
        private readonly string _timestampSegment;
        private readonly BasicsRunLogRetentionOptions _retentionOptions;
        private readonly BasicsRunLogRetentionResult _retentionResult;
        private DateTimeOffset _nextRuntimeProcessMemorySampleUtc = DateTimeOffset.MinValue;
        private int _segmentIndex;
        private long _currentFileBytes;

        private BasicsExecutionRunLog(
            string root,
            string taskSegment,
            string timestampSegment,
            string path,
            StreamWriter writer,
            BasicsExecutionPlanTraceRecord planTrace,
            BasicsBuildTraceRecord buildTrace,
            string ioAddress,
            BasicsRunLogRetentionOptions retentionOptions,
            BasicsRunLogRetentionResult retentionResult)
        {
            _root = root;
            _taskSegment = taskSegment;
            _timestampSegment = timestampSegment;
            Path = path;
            _writer = writer;
            _planTrace = planTrace;
            _buildTrace = buildTrace;
            _ioAddress = ioAddress;
            _retentionOptions = retentionOptions;
            _retentionResult = retentionResult;
        }

        public string Path { get; private set; }

        public static BasicsExecutionRunLog Create(
            BasicsEnvironmentPlan plan,
            BasicsExecutionPlanTraceRecord planTrace,
            BasicsBuildTraceRecord buildTrace,
            string ioAddress)
        {
            var root = ResolveRunLogRoot();
            Directory.CreateDirectory(root);
            var taskSegment = SanitizePathSegment(plan.SelectedTask.TaskId);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
            var retentionOptions = BasicsRunLogRetentionPolicy.FromEnvironment();
            var retentionResult = BasicsRunLogRetentionPolicy.Apply(root, DateTimeOffset.UtcNow, retentionOptions);
            var path = BuildRunLogPath(root, taskSegment, timestamp, segmentIndex: 0);
            var writer = CreateWriter(path);
            return new BasicsExecutionRunLog(
                root,
                taskSegment,
                timestamp,
                path,
                writer,
                planTrace,
                buildTrace,
                ioAddress,
                retentionOptions,
                retentionResult);
        }

        public void AppendRunStarted(BasicsEnvironmentPlan plan)
        {
            var timestampUtc = DateTimeOffset.UtcNow;
            WriteRecord(new
            {
                eventType = "run_started",
                timestampUtc,
                traceSchemaVersion = BasicsTraceability.SchemaVersion,
                ioAddress = _ioAddress,
                runLog = new
                {
                    path = Path,
                    rotationMaxFileBytes = _retentionOptions.MaxFileBytes,
                    keepMarkerPath = Path + _retentionOptions.KeepMarkerSuffix,
                    retentionOptions = _retentionOptions,
                    retentionResult = _retentionResult
                },
                runtimeProcessMemory = CaptureRuntimeProcessMemory(timestampUtc, force: true),
                taskId = plan.SelectedTask.TaskId,
                taskDisplayName = plan.SelectedTask.DisplayName,
                outputObservationMode = plan.OutputObservationMode.ToString(),
                diversityPreset = plan.DiversityPreset.ToString(),
                adaptiveDiversityEnabled = plan.AdaptiveDiversity.Enabled,
                targetAccuracy = plan.StopCriteria.TargetAccuracy,
                targetFitness = plan.StopCriteria.TargetFitness,
                maximumGenerations = plan.StopCriteria.MaximumGenerations,
                recommendedInitialPopulation = plan.Capacity.RecommendedInitialPopulationCount,
                recommendedMaxConcurrentBrains = plan.Capacity.RecommendedMaxConcurrentBrains,
                eligibleWorkerCount = plan.Capacity.EligibleWorkerCount,
                initialBrainSeeds = plan.InitialBrainSeeds.Select(seed => new
                {
                    seed.DisplayName,
                    seed.DuplicateForReproduction,
                    seed.ContentHash,
                    seed.Complexity
                }).ToArray(),
                planTrace = _planTrace,
                buildTrace = _buildTrace
            });
        }

        public void AppendSnapshot(BasicsExecutionSnapshot snapshot)
        {
            var timestampUtc = DateTimeOffset.UtcNow;
            WriteRecord(new
            {
                eventType = "snapshot",
                timestampUtc,
                runtimeProcessMemory = CaptureRuntimeProcessMemory(timestampUtc, force: false),
                snapshot.State,
                snapshot.StatusText,
                snapshot.DetailText,
                snapshot.SpeciationEpochId,
                snapshot.EvaluationFailureCount,
                snapshot.EvaluationFailureSummary,
                snapshot.Generation,
                snapshot.PopulationCount,
                snapshot.ActiveBrainCount,
                snapshot.SpeciesCount,
                snapshot.ReproductionCalls,
                snapshot.ReproductionRunsObserved,
                snapshot.CapacityUtilization,
                snapshot.OffspringBestAccuracy,
                snapshot.BestAccuracy,
                snapshot.OffspringBestFitness,
                snapshot.BestFitness,
                snapshot.MeanFitness,
                historySummary = BuildSnapshotHistorySummary(snapshot),
                latestBatchTiming = snapshot.LatestBatchTiming,
                latestGenerationTiming = snapshot.LatestGenerationTiming,
                bootstrapCandidateTraces = snapshot.BootstrapCandidateTraces.Select(trace => new
                {
                    trace.ArtifactSha256,
                    trace.SpeciesId,
                    trace.Accuracy,
                    trace.Fitness,
                    trace.Generation,
                    trace.ScoreBreakdown,
                    diagnostics = trace.Diagnostics.ToArray(),
                    bootstrapOrigin = trace.Origin
                }).ToArray(),
                bestCandidate = snapshot.BestCandidate is null
                    ? null
                    : new
                    {
                        snapshot.BestCandidate.ArtifactSha256,
                        snapshot.BestCandidate.SpeciesId,
                        snapshot.BestCandidate.Accuracy,
                        snapshot.BestCandidate.Fitness,
                        snapshot.BestCandidate.Generation,
                        snapshot.BestCandidate.AverageReadyTickCount,
                        snapshot.BestCandidate.MinReadyTickCount,
                        snapshot.BestCandidate.MedianReadyTickCount,
                        snapshot.BestCandidate.MaxReadyTickCount,
                        snapshot.BestCandidate.ScoreBreakdown,
                        bootstrapOrigin = snapshot.BestCandidate.BootstrapOrigin,
                        diagnostics = snapshot.BestCandidate.Diagnostics.ToArray()
                    }
            });
        }

        private IReadOnlyList<RuntimeProcessMemoryTraceRecord>? CaptureRuntimeProcessMemory(DateTimeOffset timestampUtc, bool force)
        {
            if (!force && timestampUtc < _nextRuntimeProcessMemorySampleUtc)
            {
                return null;
            }

            _nextRuntimeProcessMemorySampleUtc = timestampUtc + RuntimeProcessMemorySampleInterval;
            return RuntimeProcessMemoryTraceRecord.Capture(timestampUtc);
        }

        private static SnapshotHistoryTraceRecord BuildSnapshotHistorySummary(BasicsExecutionSnapshot snapshot)
            => new(
                OffspringAccuracy: BuildHistorySeriesTrace(snapshot.OffspringAccuracyHistory),
                BestAccuracy: BuildHistorySeriesTrace(snapshot.AccuracyHistory),
                OffspringBalancedAccuracy: BuildHistorySeriesTrace(snapshot.OffspringBalancedAccuracyHistory),
                BestBalancedAccuracy: BuildHistorySeriesTrace(snapshot.BalancedAccuracyHistory),
                OffspringEdgeAccuracy: BuildHistorySeriesTrace(snapshot.OffspringEdgeAccuracyHistory),
                OffspringInteriorAccuracy: BuildHistorySeriesTrace(snapshot.OffspringInteriorAccuracyHistory),
                OffspringFitness: BuildHistorySeriesTrace(snapshot.OffspringFitnessHistory),
                BestFitness: BuildHistorySeriesTrace(snapshot.BestFitnessHistory));

        private static HistorySeriesTraceRecord BuildHistorySeriesTrace(IReadOnlyList<float> history)
        {
            if (history.Count == 0)
            {
                return new HistorySeriesTraceRecord(0, null, null, null);
            }

            return new HistorySeriesTraceRecord(
                Count: history.Count,
                Latest: history[^1],
                Minimum: history.Min(),
                Maximum: history.Max());
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer.Dispose();
            }
        }

        private void WriteRecord<T>(T record)
        {
            try
            {
                var json = JsonSerializer.Serialize(record, JsonOptions);
                lock (_gate)
                {
                    WriteJsonLineLocked(json, allowRotate: true);
                }
            }
            catch
            {
                // Best-effort run logging only.
            }
        }

        private void WriteJsonLineLocked(string json, bool allowRotate)
        {
            var bytes = Encoding.UTF8.GetByteCount(json) + NewLineByteCount;
            if (allowRotate
                && _currentFileBytes > 0
                && _currentFileBytes + bytes > _retentionOptions.MaxFileBytes)
            {
                RotateLocked();
            }

            _writer.WriteLine(json);
            _currentFileBytes += bytes;
        }

        private void RotateLocked()
        {
            var previousPath = Path;
            _writer.Dispose();
            _segmentIndex++;
            Path = BuildRunLogPath(_root, _taskSegment, _timestampSegment, _segmentIndex);
            _writer = CreateWriter(Path);
            _currentFileBytes = 0;
            var timestampUtc = DateTimeOffset.UtcNow;
            var retentionResult = BasicsRunLogRetentionPolicy.Apply(
                _root,
                timestampUtc,
                _retentionOptions,
                new HashSet<string>(StringComparer.Ordinal) { Path });

            var json = JsonSerializer.Serialize(
                new
                {
                    eventType = "run_log_rotated",
                    timestampUtc,
                    previousPath,
                    path = Path,
                    segmentIndex = _segmentIndex,
                    rotationMaxFileBytes = _retentionOptions.MaxFileBytes,
                    keepMarkerPath = Path + _retentionOptions.KeepMarkerSuffix,
                    retentionResult
                },
                JsonOptions);
            WriteJsonLineLocked(json, allowRotate: false);
        }

        private static StreamWriter CreateWriter(string path)
            => new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };

        private static string BuildRunLogPath(string root, string taskSegment, string timestamp, int segmentIndex)
            => segmentIndex <= 0
                ? System.IO.Path.Combine(root, $"basics-ui-{taskSegment}-{timestamp}.jsonl")
                : System.IO.Path.Combine(root, $"basics-ui-{taskSegment}-{timestamp}.part{segmentIndex:000}.jsonl");

        private static string ResolveRunLogRoot()
        {
            var assemblyDirectory = System.IO.Path.GetDirectoryName(typeof(MainWindowViewModel).Assembly.Location) ?? string.Empty;
            foreach (var start in new[] { assemblyDirectory, AppContext.BaseDirectory, global::System.Environment.CurrentDirectory })
            {
                var current = start;
                while (!string.IsNullOrWhiteSpace(current))
                {
                    var nestedBasicsRoot = System.IO.Path.Combine(current, "Basics");
                    if (File.Exists(System.IO.Path.Combine(nestedBasicsRoot, "Basics.sln")))
                    {
                        return System.IO.Path.Combine(nestedBasicsRoot, "artifacts", "ui-runs");
                    }

                    if (File.Exists(System.IO.Path.Combine(current, "Basics.sln")))
                    {
                        return System.IO.Path.Combine(current, "artifacts", "ui-runs");
                    }

                    current = Directory.GetParent(current)?.FullName ?? string.Empty;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the Basics root for UI run logging.");
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "task";
            }

            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var chars = value
                .Trim()
                .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
                .ToArray();
            return new string(chars);
        }
    }

    private sealed record SnapshotHistoryTraceRecord(
        HistorySeriesTraceRecord OffspringAccuracy,
        HistorySeriesTraceRecord BestAccuracy,
        HistorySeriesTraceRecord OffspringBalancedAccuracy,
        HistorySeriesTraceRecord BestBalancedAccuracy,
        HistorySeriesTraceRecord OffspringEdgeAccuracy,
        HistorySeriesTraceRecord OffspringInteriorAccuracy,
        HistorySeriesTraceRecord OffspringFitness,
        HistorySeriesTraceRecord BestFitness);

    private sealed record HistorySeriesTraceRecord(
        int Count,
        float? Latest,
        float? Minimum,
        float? Maximum);

    private sealed record RuntimeProcessMemoryTraceRecord(
        DateTimeOffset SampledAtUtc,
        int ProcessId,
        string ProcessName,
        string RuntimeRole,
        long? WorkingSetBytes,
        long? PrivateMemoryBytes,
        long? VirtualMemoryBytes,
        bool MatchedCommandLine)
    {
        private static readonly string[] RuntimeRoleTokens =
        {
            "Nbn.Runtime.IO",
            "Nbn.Runtime.WorkerNode",
            "Nbn.Runtime.HiveMind",
            "Nbn.Runtime.Reproduction",
            "Nbn.Runtime.Speciation",
            "Nbn.Tools.Workbench",
            "Nbn.Demos.Basics.Ui",
            "Basics.Ui"
        };

        public static IReadOnlyList<RuntimeProcessMemoryTraceRecord> Capture(DateTimeOffset sampledAtUtc)
        {
            var records = new List<RuntimeProcessMemoryTraceRecord>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var processName = process.ProcessName;
                    var commandLine = TryReadLinuxCommandLine(process.Id);
                    var role = ResolveRuntimeRole(processName, commandLine);
                    if (role is null)
                    {
                        continue;
                    }

                    var matchedCommandLine = commandLine?.Contains(role, StringComparison.OrdinalIgnoreCase) ?? false;

                    process.Refresh();
                    records.Add(new RuntimeProcessMemoryTraceRecord(
                        sampledAtUtc,
                        process.Id,
                        processName,
                        role,
                        TryReadInt64(() => process.WorkingSet64),
                        TryReadInt64(() => process.PrivateMemorySize64),
                        TryReadInt64(() => process.VirtualMemorySize64),
                        matchedCommandLine));
                }
                catch
                {
                    // Process enumeration races with process exit; skip short-lived entries.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return records
                .OrderBy(static record => record.RuntimeRole, StringComparer.Ordinal)
                .ThenBy(static record => record.ProcessId)
                .ToArray();
        }

        private static string? ResolveRuntimeRole(string processName, string? commandLine)
        {
            foreach (var token in RuntimeRoleTokens)
            {
                if (processName.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || (commandLine?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    return token;
                }
            }

            return null;
        }

        private static long? TryReadInt64(Func<long> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryReadLinuxCommandLine(int processId)
        {
            var path = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/cmdline";
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0)
                {
                    return null;
                }

                var commandLine = System.Text.Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
                if (string.IsNullOrWhiteSpace(commandLine))
                {
                    return null;
                }

                return commandLine.Length <= 512 ? commandLine : commandLine[..512];
            }
            catch
            {
                return null;
            }
        }
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

public sealed record DiversityPresetOption(
    BasicsDiversityPreset Value,
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

public sealed class InitialBrainSeedItemViewModel : ViewModelBase
{
    private bool _duplicateForReproduction;

    public InitialBrainSeedItemViewModel(
        string displayName,
        string? localPath,
        byte[] definitionBytes,
        string? snapshotLocalPath,
        byte[]? snapshotBytes,
        string contentHash,
        BasicsDefinitionComplexitySummary complexity,
        bool duplicateForReproduction,
        RelayCommand removeCommand)
    {
        DisplayName = displayName;
        LocalPath = localPath;
        DefinitionBytes = definitionBytes;
        SnapshotLocalPath = snapshotLocalPath;
        SnapshotBytes = snapshotBytes;
        ContentHash = contentHash;
        Complexity = complexity;
        _duplicateForReproduction = duplicateForReproduction;
        RemoveCommand = removeCommand;
    }

    public string DisplayName { get; }

    public string? LocalPath { get; }

    public byte[] DefinitionBytes { get; }

    public string? SnapshotLocalPath { get; }

    public byte[]? SnapshotBytes { get; }

    public string ContentHash { get; }

    public BasicsDefinitionComplexitySummary Complexity { get; }

    public RelayCommand RemoveCommand { get; }

    public bool DuplicateForReproduction
    {
        get => _duplicateForReproduction;
        set => SetProperty(ref _duplicateForReproduction, value);
    }
}

public sealed record WinnerExportBestCandidateTrace(
    string ArtifactSha256,
    string SpeciesId,
    int Generation,
    float Accuracy,
    float Fitness,
    IReadOnlyDictionary<string, float> ScoreBreakdown,
    IReadOnlyList<string> Diagnostics,
    BasicsBootstrapOrigin? BootstrapOrigin,
    float? AverageReadyTickCount,
    float? MinReadyTickCount,
    float? MedianReadyTickCount,
    float? MaxReadyTickCount,
    float? ReadyTickStdDev);

public sealed record WinnerExportTraceabilityRecord(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    string TaskId,
    string TaskDisplayName,
    string IoAddress,
    string? RunLogPath,
    string DefinitionPath,
    string DefinitionArtifactSha256,
    string? SnapshotPath,
    string? SnapshotArtifactSha256,
    bool UsedLiveDefinitionExport,
    bool UsedLiveSnapshotCapture,
    BasicsExecutionPlanTraceRecord? Plan,
    BasicsBuildTraceRecord Build,
    WinnerExportBestCandidateTrace? BestCandidate);
