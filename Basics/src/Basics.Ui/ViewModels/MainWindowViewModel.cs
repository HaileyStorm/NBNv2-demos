using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Avalonia;
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
    private BasicsExecutionSession? _executionSession;
    private CancellationTokenSource? _executionCts;
    private BasicsEnvironmentPlan? _lastPlan;
    private bool _suppressValidationRefresh;
    private bool _isExecutionRunning;
    private bool _isWorkerLauncherBusy;
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
    private string _minimumPopulationOverrideText = string.Empty;
    private string _maximumPopulationOverrideText = string.Empty;
    private string _reproductionRunCountOverrideText = string.Empty;
    private string _maxConcurrentBrainsOverrideText = string.Empty;
    private string _recommendedInitialPopulationText = "—";
    private string _recommendedRunCountText = "—";
    private string _recommendedMaxConcurrentBrainsText = "—";
    private string _targetAccuracyText = "1.0";
    private string _targetFitnessText = "0.999";
    private string _maximumGenerationsText = string.Empty;
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
    private string _winnerExportStatus = "No best-so-far brain retained.";
    private string _winnerExportDetail = "The strongest evaluated candidate is retained for export when a run finishes or reaches a stop target.";
    private string _workerCountText = "1";
    private string _workerBasePortText = "12041";
    private string _workerStoragePctText = "95";
    private string _workerLauncherStatus = "No workers launched from Basics UI.";
    private string _workerLauncherDetail = "Starts local WorkerNode processes on consecutive loopback ports. Shared-port multi-worker launch is not supported here yet.";
    private string _initialBrainSeedStatus = "No initial brains uploaded.";
    private ArtifactRef? _winnerDefinitionArtifact;
    private ArtifactRef? _winnerSnapshotArtifact;
    private Guid _retainedWinnerBrainId;
    private TaskOption? _selectedTask;
    private StrengthSourceOption? _selectedStrengthSource;
    private OutputObservationModeOption? _selectedOutputObservationMode;
    private DiversityPresetOption? _selectedDiversityPreset;

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
        InitialBrainSeeds = new ObservableCollection<InitialBrainSeedItemViewModel>();

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
        FetchCapacityCommand = new AsyncRelayCommand(FetchCapacityAsync, CanBuildPlan);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
        ExportDefinitionCommand = new AsyncRelayCommand(ExportWinningDefinitionAsync, CanExportWinnerDefinition);
        ExportSnapshotCommand = new AsyncRelayCommand(ExportWinningSnapshotAsync, CanExportWinnerSnapshot);
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

    public ObservableCollection<InitialBrainSeedItemViewModel> InitialBrainSeeds { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand FetchCapacityCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ExportDefinitionCommand { get; }

    public AsyncRelayCommand ExportSnapshotCommand { get; }

    public RelayCommand ApplySuggestedBoundsCommand { get; }

    public AsyncRelayCommand StartWorkersCommand { get; }

    public AsyncRelayCommand StopWorkersCommand { get; }

    public AsyncRelayCommand AddInitialBrainsCommand { get; }

    public RelayCommand ClearInitialBrainsCommand { get; }

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

    public string MaximumGenerationsText
    {
        get => _maximumGenerationsText;
        set => SetProperty(ref _maximumGenerationsText, value);
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

    public bool HasAccuracyChartData => _accuracyHistory.Count > 0;

    public bool HasFitnessChartData => _fitnessHistory.Count > 0;

    public bool ShowAccuracyEmptyState => !HasAccuracyChartData;

    public bool ShowFitnessEmptyState => !HasFitnessChartData;

    public IReadOnlyList<Point> AccuracyChartPoints => BuildChartPoints(_accuracyHistory);

    public IReadOnlyList<Point> FitnessChartPoints => BuildChartPoints(_fitnessHistory);

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
        WorkerLauncherDetail = $"Launching {request.WorkerCount} local worker process(es) from Basics UI.";
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

            await StopExecutionAsync().ConfigureAwait(false);

            if (!TaskPluginRegistry.TryGet(options.SelectedTask.TaskId, out var plugin))
            {
                _dispatcher.Post(() =>
                {
                    ExecutionStatus = "Start blocked.";
                    ExecutionDetail = $"{options.SelectedTask.DisplayName} is not implemented yet.";
                });
                return;
            }

            if (!await RestartRuntimeClientForRunAsync(runtimeOptions).ConfigureAwait(false))
            {
                return;
            }

            var planner = new BasicsEnvironmentPlanner(_runtimeClient);
            var plan = await planner.BuildPlanAsync(options).ConfigureAwait(false);
            _dispatcher.Post(() => ApplyPlan(plan));

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
                    ExecutionDetail = $"Launching {plan.SelectedTask.DisplayName} with template family {plan.SeedTemplate.TemplateId}. Diversity {FormatDiversityPreset(plan.DiversityPreset)} with adaptive stall boost {(plan.AdaptiveDiversity.Enabled ? "on" : "off")}. Stop target: accuracy >= {plan.StopCriteria.TargetAccuracy:0.###}, fitness >= {plan.StopCriteria.TargetFitness:0.###}, generation limit {FormatGenerationLimit(plan.StopCriteria.MaximumGenerations)}.";
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
        MetricsSecondaryStatus = $"Population {plan.Capacity.RecommendedInitialPopulationCount}, concurrent {plan.Capacity.RecommendedMaxConcurrentBrains}, base run count {plan.Capacity.RecommendedReproductionRunCount}, output mode {FormatOutputObservationMode(plan.OutputObservationMode)}, diversity {FormatDiversityPreset(plan.DiversityPreset)}, stop target {plan.StopCriteria.TargetAccuracy:0.###}/{plan.StopCriteria.TargetFitness:0.###}, generation limit {FormatGenerationLimit(plan.StopCriteria.MaximumGenerations)}.";
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

    private bool CanStartWorkers() => !IsWorkerLauncherBusy;

    private bool CanStopWorkers() => !IsWorkerLauncherBusy && _workerProcessService.LaunchedWorkerCount > 0;

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

        if (!int.TryParse(WorkerBasePortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var basePort) || basePort <= 0)
        {
            failureMessage = "First worker port must be a positive integer.";
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

        request = new BasicsLocalWorkerLaunchRequest(
            WorkerCount: workerCount,
            BasePort: basePort,
            StoragePercent: storagePercent,
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
            MaximumGenerations = ParseOptionalInt(MaximumGenerationsText, "Maximum generations", errors)
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
            DiversityPreset = diversityPreset,
            AdaptiveDiversity = new BasicsAdaptiveDiversityOptions(),
            Reproduction = new BasicsReproductionPolicy
            {
                Config = reproductionConfig,
                StrengthSource = SelectedStrengthSource?.Value ?? Repro.StrengthSource.StrengthBaseOnly
            },
            Scheduling = scheduling,
            StopCriteria = stopCriteria,
            InitialBrainSeeds = InitialBrainSeeds.Select(seed => new BasicsInitialBrainSeed(
                seed.DisplayName,
                seed.DefinitionBytes.ToArray(),
                seed.DuplicateForReproduction,
                seed.Complexity)).ToArray()
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
                ExecutionDetail = "Releasing the retained best-so-far brain.";
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
        MetricsSecondaryStatus = resolvedDetail;
        LastPlanSummary = $"Generation {snapshot.Generation} · population {snapshot.PopulationCount} · species {snapshot.SpeciesCount}.";

        ReplaceHistory(_accuracyHistory, snapshot.AccuracyHistory);
        ReplaceHistory(_fitnessHistory, snapshot.BestFitnessHistory);
        UpdateChartBindings();

        var displayedAccuracy = ResolveLatestMetric(snapshot.AccuracyHistory, snapshot.BestAccuracy);
        var displayedBestAccuracy = ResolvePeakMetric(snapshot.AccuracyHistory, snapshot.BestAccuracy);
        var displayedBestFitness = ResolvePeakMetric(snapshot.BestFitnessHistory, snapshot.BestFitness);

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
            displayedAccuracy.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null
                ? "No evaluated generation yet."
                : $"Most recent evaluated-generation accuracy; best-so-far species {snapshot.BestCandidate.SpeciesId}.");
        UpdateMetricSummary(
            BasicsMetricId.BestAccuracy,
            displayedBestAccuracy.ToString("0.###", CultureInfo.InvariantCulture),
            snapshot.BestCandidate is null
                ? "No successful evaluation yet."
                : $"Best-so-far artifact {snapshot.BestCandidate.ArtifactSha256[..Math.Min(12, snapshot.BestCandidate.ArtifactSha256.Length)]}...");
        UpdateMetricSummary(
            BasicsMetricId.BestFitness,
            displayedBestFitness.ToString("0.###", CultureInfo.InvariantCulture),
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
        UpdateMetricSummary(
            BasicsMetricId.LatestBatchDuration,
            snapshot.LatestBatchTiming is null
                ? "—"
                : FormatRuntimeSeconds(snapshot.LatestBatchTiming.BatchDurationSeconds),
            snapshot.LatestBatchTiming is null
                ? "Instrumentation appears after the first evaluated batch."
                : $"Batch {snapshot.LatestBatchTiming.BatchIndex}/{snapshot.LatestBatchTiming.BatchCount}: queue {FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageQueueWaitSeconds)}/brain, spawn {FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageSpawnRequestSeconds)}/brain.");
        UpdateMetricSummary(
            BasicsMetricId.LatestSetupDuration,
            snapshot.LatestBatchTiming is null
                ? "—"
                : FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageSetupSeconds),
            snapshot.LatestBatchTiming is null
                ? "Instrumentation appears after the first evaluated batch."
                : "Average setup-to-ready time per brain: brain-info/configure/subscribe/prime.");
        UpdateMetricSummary(
            BasicsMetricId.LatestObservationDuration,
            snapshot.LatestBatchTiming is null
                ? "—"
                : FormatRuntimeSeconds(snapshot.LatestBatchTiming.AverageObservationSeconds),
            snapshot.LatestBatchTiming is null
                ? "Instrumentation appears after the first evaluated batch."
                : "Average sample-observation time per brain; long waits here usually mean output timeouts/retries.");

        if (snapshot.State == BasicsExecutionState.Succeeded && CanUseBestCandidateForExport(snapshot.BestCandidate))
        {
            ApplyWinnerArtifacts(snapshot.BestCandidate!);
        }
        else if (snapshot.State is BasicsExecutionState.Failed or BasicsExecutionState.Stopped)
        {
            if (CanUseBestCandidateForExport(snapshot.BestCandidate))
            {
                ApplyWinnerArtifacts(snapshot.BestCandidate!);
            }
            else
            {
                ClearWinnerState(clearArtifacts: false);
            }
        }
    }

    private async Task ExportWinningDefinitionAsync()
    {
        if (!HasArtifactRef(_winnerDefinitionArtifact))
        {
            WinnerExportStatus = "No best-so-far definition available.";
            WinnerExportDetail = "Run at least one evaluated generation before exporting.";
            return;
        }

        try
        {
            var path = await _artifactExportService.ExportAsync(
                    _winnerDefinitionArtifact!,
                    title: "Export best definition",
                    suggestedFileName: BuildSuggestedArtifactFileName("best", "nbn"))
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                WinnerExportStatus = path is null ? "Definition export canceled." : "Best-so-far definition exported.";
                WinnerExportDetail = path is null
                    ? "The best-so-far .nbn artifact is still available for export."
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
            WinnerExportStatus = "No best-so-far snapshot available.";
            WinnerExportDetail = "Snapshot export is only available when runtime state was captured for the retained best-so-far brain.";
            return;
        }

        try
        {
            var path = await _artifactExportService.ExportAsync(
                    _winnerSnapshotArtifact!,
                    title: "Export best snapshot",
                    suggestedFileName: BuildSuggestedArtifactFileName("best-state", "nbs"))
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                WinnerExportStatus = path is null ? "Snapshot export canceled." : "Best-so-far snapshot exported.";
                WinnerExportDetail = path is null
                    ? "The best-so-far .nbs artifact is still available for export."
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
            WinnerExportStatus = "No best-so-far brain retained.";
            WinnerExportDetail = "The strongest evaluated candidate is retained for export when a run finishes or reaches a stop target.";
            return;
        }

        WinnerExportStatus = _retainedWinnerBrainId != Guid.Empty
            ? "Best-so-far brain retained for export."
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
            detailParts.Add("No snapshot artifact was produced for this retained candidate.");
        }

        if (_retainedWinnerBrainId != Guid.Empty)
        {
            detailParts.Add($"Live best brain {_retainedWinnerBrainId:N} stays active until Stop, Disconnect, or the next run.");
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

    private static float ResolveLatestMetric(IReadOnlyList<float> history, float fallbackValue)
        => history.Count == 0 ? fallbackValue : history[^1];

    private static float ResolvePeakMetric(IReadOnlyList<float> history, float fallbackValue)
        => history.Count == 0 ? fallbackValue : Math.Max(fallbackValue, history.Max());

    private static bool CanUseBestCandidateForExport(BasicsExecutionBestCandidateSummary? bestCandidate)
        => bestCandidate is not null
           && bestCandidate.Diagnostics.Count == 0
           && HasArtifactRef(bestCandidate.DefinitionArtifact);

    private static string FormatGenerationLimit(int? maximumGenerations)
        => maximumGenerations.HasValue
            ? maximumGenerations.Value.ToString(CultureInfo.InvariantCulture)
            : "unlimited";

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

    private static IReadOnlyList<Point> BuildChartPoints(IReadOnlyList<float> history)
    {
        if (history.Count == 0)
        {
            return Array.Empty<Point>();
        }

        const float width = 320f;
        const float height = 140f;
        if (history.Count == 1)
        {
            var y = height - (Math.Clamp(history[0], 0f, 1f) * height);
            return new[] { new Point(0d, y) };
        }

        var stepX = width / (history.Count - 1f);
        return history.Select((value, index) =>
        {
            var x = index * stepX;
            var y = height - (Math.Clamp(value, 0f, 1f) * height);
            return new Point(x, y);
        }).ToArray();
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

    private static string BuildBatchTimingStatus(BasicsExecutionBatchTimingSummary timing)
    {
        var detail = $"Last batch {timing.BatchIndex}/{timing.BatchCount}: total {FormatRuntimeSeconds(timing.BatchDurationSeconds)}, queue {FormatRuntimeSeconds(timing.AverageQueueWaitSeconds)}/brain, spawn {FormatRuntimeSeconds(timing.AverageSpawnRequestSeconds)}/brain, setup {FormatRuntimeSeconds(timing.AverageSetupSeconds)}/brain, observe {FormatRuntimeSeconds(timing.AverageObservationSeconds)}/brain.";
        if (!string.IsNullOrWhiteSpace(timing.FailureSummary))
        {
            detail += $" Failures: {timing.FailureSummary}.";
        }

        return detail;
    }

    private static string BuildGenerationTimingStatus(BasicsExecutionGenerationTimingSummary timing)
    {
        var detail = $"Generation timing: total {FormatRuntimeSeconds(timing.TotalDurationSeconds)}, avg batch {FormatRuntimeSeconds(timing.AverageBatchDurationSeconds)}, setup {FormatRuntimeSeconds(timing.AverageSetupSeconds)}/brain, observe {FormatRuntimeSeconds(timing.AverageObservationSeconds)}/brain.";
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

    private static IEnumerable<DiversityPresetOption> BuildDiversityPresets()
    {
        yield return new DiversityPresetOption(BasicsDiversityPreset.Low, "Low", "Conservative reproduction and low exploration.");
        yield return new DiversityPresetOption(BasicsDiversityPreset.Medium, "Medium", "Balanced exploration and mutation pressure.");
        yield return new DiversityPresetOption(BasicsDiversityPreset.High, "High", "More structural churn and stronger diversity pressure.");
        yield return new DiversityPresetOption(BasicsDiversityPreset.Extreme, "Extreme", "Maximum exploration pressure for stubborn plateaus.");
    }

    private static IEnumerable<MetricSummaryItemViewModel> BuildMetricSummaryItems()
    {
        yield return new MetricSummaryItemViewModel(BasicsMetricId.Accuracy, "Latest accuracy", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestAccuracy, "Best accuracy", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.BestFitness, "Best fitness", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.MeanFitness, "Mean fitness", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.PopulationCount, "Population", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ActiveBrainCount, "Active brains", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.SpeciesCount, "Species", "1 seed family", "Template-anchored bootstrap assumption.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ReproductionCalls, "Reproduction calls", "—", "No runtime samples yet.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.ReproductionRunsObserved, "Runs observed", "—", "Plan-derived once capacity is fetched.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.CapacityUtilization, "Capacity score", "—", "Filled from IO capacity planning.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.LatestBatchDuration, "Last batch", "—", "Instrumentation appears after the first evaluated batch.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.LatestSetupDuration, "Setup/brain", "—", "Average brain-info/configure/subscribe/prime time per evaluated batch.");
        yield return new MetricSummaryItemViewModel(BasicsMetricId.LatestObservationDuration, "Observe/brain", "—", "Average sample-observation time per evaluated batch.");
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
        string contentHash,
        BasicsDefinitionComplexitySummary complexity,
        bool duplicateForReproduction,
        RelayCommand removeCommand)
    {
        DisplayName = displayName;
        LocalPath = localPath;
        DefinitionBytes = definitionBytes;
        ContentHash = contentHash;
        Complexity = complexity;
        _duplicateForReproduction = duplicateForReproduction;
        RemoveCommand = removeCommand;
    }

    public string DisplayName { get; }

    public string? LocalPath { get; }

    public byte[] DefinitionBytes { get; }

    public string ContentHash { get; }

    public BasicsDefinitionComplexitySummary Complexity { get; }

    public RelayCommand RemoveCommand { get; }

    public bool DuplicateForReproduction
    {
        get => _duplicateForReproduction;
        set => SetProperty(ref _duplicateForReproduction, value);
    }
}
