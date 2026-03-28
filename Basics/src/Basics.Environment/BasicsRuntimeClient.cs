using Nbn.Proto;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Proto.Repro;
using Nbn.Proto.Settings;
using Nbn.Proto.Speciation;
using Nbn.Shared;
using Proto;
using Proto.Remote;
using Proto.Remote.GrpcNet;

namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsRuntimeClientOptions
{
    public string IoAddress { get; init; } = "127.0.0.1:12020";
    public string IoGatewayName { get; init; } = "io-gateway";
    public string BindHost { get; init; } = NetworkAddressDefaults.DefaultBindHost;
    public int Port { get; init; } = 12074;
    public string? AdvertiseHost { get; init; }
    public int? AdvertisePort { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public interface IBasicsRuntimeClient : IAsyncDisposable
{
    Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default);

    Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default);

    Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task<SpawnBrainViaIOAck?> SpawnBrainAsync(SpawnBrain request, CancellationToken cancellationToken = default);
}

public sealed class BasicsRuntimeClient : IBasicsRuntimeClient
{
    private readonly ActorSystem _system;
    private readonly PID _ioPid;
    private readonly TimeSpan _requestTimeout;
    private bool _disposed;

    private BasicsRuntimeClient(ActorSystem system, PID ioPid, TimeSpan requestTimeout)
    {
        _system = system;
        _ioPid = ioPid;
        _requestTimeout = requestTimeout;
    }

    public static async Task<BasicsRuntimeClient> StartAsync(
        BasicsRuntimeClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var system = new ActorSystem();
        system.WithRemote(BuildRemoteConfig(
            options.BindHost,
            options.Port,
            options.AdvertiseHost,
            options.AdvertisePort));
        await system.Remote().StartAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return new BasicsRuntimeClient(
            system,
            new PID(options.IoAddress.Trim(), options.IoGatewayName.Trim()),
            options.RequestTimeout);
    }

    public async Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(clientName))
        {
            throw new ArgumentException("Client name is required.", nameof(clientName));
        }

        try
        {
            return await _system.Root.RequestAsync<ConnectAck>(
                    _ioPid,
                    new Connect { ClientName = clientName.Trim() },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            return await _system.Root.RequestAsync<PlacementWorkerInventoryResult>(
                    _ioPid,
                    new GetPlacementWorkerInventory(),
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await _system.Root.RequestAsync<BrainInfo>(
                    _ioPid,
                    new BrainInfoRequest { BrainId = brainId.ToProtoUuid() },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<SpawnBrainViaIOAck?> SpawnBrainAsync(
        SpawnBrain request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await _system.Root.RequestAsync<SpawnBrainViaIOAck>(
                    _ioPid,
                    new SpawnBrainViaIO { Request = request },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _system.ShutdownAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static RemoteConfig BuildRemoteConfig(
        string bindHost,
        int port,
        string? advertisedHost,
        int? advertisedPort)
    {
        RemoteConfig config;
        if (NetworkAddressDefaults.IsAllInterfaces(bindHost))
        {
            var resolvedAdvertiseHost = NetworkAddressDefaults.ResolveAdvertisedHost(bindHost, advertisedHost);
            config = RemoteConfig.BindToAllInterfaces(resolvedAdvertiseHost, port);
        }
        else if (NetworkAddressDefaults.IsLoopbackHost(bindHost))
        {
            config = RemoteConfig.BindToLocalhost(port);
        }
        else
        {
            config = RemoteConfig.BindTo(bindHost, port);
        }

        if (!string.IsNullOrWhiteSpace(advertisedHost))
        {
            config = config.WithAdvertisedHost(advertisedHost.Trim());
        }

        if (advertisedPort.HasValue)
        {
            config = config.WithAdvertisedPort(advertisedPort.Value);
        }

        return config.WithProtoMessages(
            NbnCommonReflection.Descriptor,
            NbnControlReflection.Descriptor,
            NbnIoReflection.Descriptor,
            NbnSettingsReflection.Descriptor,
            NbnReproReflection.Descriptor,
            NbnSpeciationReflection.Descriptor);
    }
}
