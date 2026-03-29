using Microsoft.Data.Sqlite;
using Nbn.Demos.Basics.Environment;
using Nbn.Proto;
using Nbn.Proto.Control;
using Nbn.Proto.Debug;
using Nbn.Proto.Io;
using Nbn.Proto.Repro;
using Nbn.Proto.Settings;
using Nbn.Proto.Signal;
using Nbn.Proto.Viz;
using Nbn.Runtime.HiveMind;
using Nbn.Runtime.IO;
using Nbn.Runtime.Reproduction;
using Nbn.Runtime.Speciation;
using Nbn.Runtime.WorkerNode;
using Nbn.Shared;
using Proto;
using Proto.Remote;
using Proto.Remote.GrpcNet;
using ProtoSettings = Nbn.Proto.Settings;
using ProtoSpec = Nbn.Proto.Speciation;

namespace Nbn.Demos.Basics.Harness;

internal sealed record LocalSmokeRuntimeHostOptions
{
    public string BindHost { get; init; } = "127.0.0.1";
    public int IoPort { get; init; } = 12050;
    public float TargetTickHz { get; init; } = 30f;
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

internal sealed class LocalSmokeRuntimeHost : IAsyncDisposable
{
    private readonly ActorSystem _system;
    private readonly string _runtimeRoot;

    private LocalSmokeRuntimeHost(
        ActorSystem system,
        string runtimeRoot,
        string artifactRoot,
        int ioPort,
        PID hiveMindPid,
        PID ioGatewayPid,
        PID reproductionManagerPid,
        PID speciationManagerPid,
        PID workerPid,
        Guid workerId)
    {
        _system = system;
        _runtimeRoot = runtimeRoot;
        ArtifactRoot = artifactRoot;
        IoPort = ioPort;
        HiveMindPid = hiveMindPid;
        IoGatewayPid = ioGatewayPid;
        ReproductionManagerPid = reproductionManagerPid;
        SpeciationManagerPid = speciationManagerPid;
        WorkerPid = workerPid;
        WorkerId = workerId;
    }

    public string ArtifactRoot { get; }

    public int IoPort { get; }

    public string IoAddress => $"127.0.0.1:{IoPort}";

    public string IoGatewayName => IoNames.Gateway;

    public PID HiveMindPid { get; }

    public PID IoGatewayPid { get; }

    public PID ReproductionManagerPid { get; }

    public PID SpeciationManagerPid { get; }

    public PID WorkerPid { get; }

    public Guid WorkerId { get; }

    public static async Task<LocalSmokeRuntimeHost> StartAsync(
        LocalSmokeRuntimeHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimeRoot = Path.Combine(Path.GetTempPath(), "nbn-basics-local-smoke", Guid.NewGuid().ToString("N"));
        var artifactRoot = Path.Combine(runtimeRoot, "artifacts");
        var speciationRoot = Path.Combine(runtimeRoot, "speciation");
        Directory.CreateDirectory(artifactRoot);
        Directory.CreateDirectory(speciationRoot);

        var system = new ActorSystem();
        system.WithRemote(
            RemoteConfig.BindToLocalhost(options.IoPort).WithProtoMessages(
                NbnCommonReflection.Descriptor,
                NbnControlReflection.Descriptor,
                    NbnIoReflection.Descriptor,
                    NbnReproReflection.Descriptor,
                    NbnSignalsReflection.Descriptor,
                    NbnSettingsReflection.Descriptor,
                    ProtoSpec.NbnSpeciationReflection.Descriptor,
                    NbnDebugReflection.Descriptor,
                    NbnVizReflection.Descriptor));
        await system.Remote().StartAsync().ConfigureAwait(false);

        var root = system.Root;
        var localIoPid = new PID(string.Empty, IoNames.Gateway);
        var localReproPid = new PID(string.Empty, ReproductionNames.Manager);
        var localSpeciationPid = new PID(string.Empty, SpeciationNames.Manager);

        var availability = new WorkerResourceAvailability(
            cpuPercent: 100,
            ramPercent: 100,
            storagePercent: 100,
            gpuComputePercent: 100,
            gpuVramPercent: 100);
        var capabilityProvider = new WorkerNodeCapabilityProvider(availability: availability);

        var hiveMindPid = root.SpawnNamed(
            Props.FromProducer(() => new HiveMindActor(CreateHiveMindOptions(options.TargetTickHz), ioPid: localIoPid)),
            HiveMindNames.HiveMind);

        var reproductionManagerPid = root.SpawnNamed(
            Props.FromProducer(() => new ReproductionManagerActor(localIoPid)),
            ReproductionNames.Manager);

        var speciationStore = new SpeciationStore(Path.Combine(speciationRoot, "speciation.db"));
        var speciationRuntimeConfig = SpeciationOptions.FromArgs(Array.Empty<string>()).ToRuntimeConfig();
        var speciationManagerPid = root.SpawnNamed(
            Props.FromProducer(() => new SpeciationManagerActor(
                speciationStore,
                speciationRuntimeConfig,
                settingsPid: null,
                reproductionManagerPid: localReproPid,
                ioGatewayPid: localIoPid)),
            SpeciationNames.Manager);

        var ioGatewayPid = root.SpawnNamed(
            Props.FromProducer(() => new IoGatewayActor(
                CreateIoOptions(options.IoPort),
                hiveMindPid: hiveMindPid,
                reproPid: reproductionManagerPid,
                speciationPid: speciationManagerPid)),
            IoNames.Gateway);

        var workerId = Guid.NewGuid();
        var workerPid = root.SpawnNamed(
            Props.FromProducer(() => new WorkerNodeActor(
                workerId,
                string.Empty,
                artifactRootPath: artifactRoot,
                capabilitySnapshotProvider: capabilityProvider.GetCapabilities,
                resourceAvailability: availability)),
            "worker-node");

        PrimeWorkerDiscoveryEndpoints(root, workerPid, hiveMindPid.Id, ioGatewayPid.Id);
        PrimeWorkers(root, hiveMindPid, workerPid, workerId, capabilityProvider.GetCapabilities());
        await WaitForWorkerReadinessAsync(root, workerPid, options.ReadinessTimeout, cancellationToken).ConfigureAwait(false);

        return new LocalSmokeRuntimeHost(
            system,
            runtimeRoot,
            artifactRoot,
            options.IoPort,
            hiveMindPid,
            ioGatewayPid,
            reproductionManagerPid,
            speciationManagerPid,
            workerPid,
            workerId);
    }

    public async Task WaitForIoReadinessAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await WaitForAsync(
                async () =>
                {
                    var ack = await _system.Root.RequestAsync<ConnectAck>(
                            IoGatewayPid,
                            new Connect
                            {
                                ClientName = "nbn.basics.local-smoke.probe"
                            },
                            TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                    if (ack is null)
                    {
                        return false;
                    }

                    var inventory = await _system.Root.RequestAsync<PlacementWorkerInventoryResult>(
                            IoGatewayPid,
                            new GetPlacementWorkerInventory(),
                            TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                    return inventory is not null && inventory.Success;
                },
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _system.Remote().ShutdownAsync(true).ConfigureAwait(false);
        await _system.ShutdownAsync().ConfigureAwait(false);

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }
    }

    private static IoOptions CreateIoOptions(int ioPort)
        => new(
            BindHost: "127.0.0.1",
            Port: ioPort,
            AdvertisedHost: null,
            AdvertisedPort: null,
            GatewayName: IoNames.Gateway,
            ServerName: "nbn.io.local-smoke",
            SettingsHost: null,
            SettingsPort: 0,
            SettingsName: "SettingsMonitor",
            HiveMindAddress: null,
            HiveMindName: null,
            ReproAddress: null,
            ReproName: null,
            SpeciationAddress: null,
            SpeciationName: null);

    private static HiveMindOptions CreateHiveMindOptions(float targetTickHz)
        => new(
            BindHost: "127.0.0.1",
            Port: 0,
            AdvertisedHost: null,
            AdvertisedPort: null,
            TargetTickHz: targetTickHz,
            MinTickHz: Math.Max(1f, Math.Min(10f, targetTickHz)),
            ComputeTimeoutMs: 500,
            DeliverTimeoutMs: 500,
            BackpressureDecay: 0.9f,
            BackpressureRecovery: 1.1f,
            LateBackpressureThreshold: 2,
            TimeoutRescheduleThreshold: 3,
            TimeoutPauseThreshold: 6,
            RescheduleMinTicks: 10,
            RescheduleMinMinutes: 1,
            RescheduleQuietMs: 50,
            RescheduleSimulatedMs: 50,
            AutoStart: true,
            EnableOpenTelemetry: false,
            EnableOtelMetrics: false,
            EnableOtelTraces: false,
            EnableOtelConsoleExporter: false,
            OtlpEndpoint: null,
            ServiceName: "nbn.hivemind.local-smoke",
            SettingsDbPath: null,
            SettingsHost: null,
            SettingsPort: 0,
            SettingsName: "SettingsMonitor",
            IoAddress: null,
            IoName: null,
            WorkerInventoryRefreshMs: 2_000,
            WorkerInventoryStaleAfterMs: 10_000,
            PlacementAssignmentTimeoutMs: 1_000,
            PlacementAssignmentRetryBackoffMs: 10,
            PlacementAssignmentMaxRetries: 1,
            PlacementReconcileTimeoutMs: 1_000);

    private static void PrimeWorkerDiscoveryEndpoints(IRootContext root, PID workerPid, string hiveName, string ioName)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var known = new Dictionary<string, ServiceEndpointRegistration>(StringComparer.Ordinal)
        {
            [ServiceEndpointSettings.HiveMindKey] = new(
                ServiceEndpointSettings.HiveMindKey,
                new ServiceEndpoint(string.Empty, hiveName),
                nowMs),
            [ServiceEndpointSettings.IoGatewayKey] = new(
                ServiceEndpointSettings.IoGatewayKey,
                new ServiceEndpoint(string.Empty, ioName),
                nowMs)
        };

        root.Send(workerPid, new WorkerNodeActor.DiscoverySnapshotApplied(known));
    }

    private static void PrimeWorkers(
        IRootContext root,
        PID hiveMindPid,
        PID workerPid,
        Guid workerId,
        ProtoSettings.NodeCapabilities capabilities)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        root.Send(hiveMindPid, new ProtoSettings.WorkerInventorySnapshotResponse
        {
            SnapshotMs = (ulong)nowMs,
            Workers =
            {
                new ProtoSettings.WorkerReadinessCapability
                {
                    NodeId = workerId.ToProtoUuid(),
                    Address = string.Empty,
                    RootActorName = workerPid.Id,
                    IsAlive = true,
                    IsReady = true,
                    LastSeenMs = (ulong)nowMs,
                    HasCapabilities = true,
                    CapabilityTimeMs = (ulong)nowMs,
                    Capabilities = capabilities.Clone()
                }
            }
        });
    }

    private static async Task WaitForWorkerReadinessAsync(
        IRootContext root,
        PID workerPid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await WaitForAsync(
                async () =>
                {
                    var snapshot = await root.RequestAsync<WorkerNodeActor.WorkerNodeSnapshot>(
                            workerPid,
                            new WorkerNodeActor.GetWorkerNodeSnapshot())
                        .ConfigureAwait(false);
                    return snapshot.IoGatewayEndpoint.HasValue && snapshot.HiveMindEndpoint.HasValue;
                },
                timeout,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task WaitForAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCancellation.Token, cancellationToken);

        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (await predicate().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(200, linked.Token).ConfigureAwait(false);
        }
    }
}
