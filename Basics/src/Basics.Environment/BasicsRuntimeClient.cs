using System.Collections.Concurrent;
using System.Threading.Channels;
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
    public string IoAddress { get; init; } = "127.0.0.1:12050";
    public string IoGatewayName { get; init; } = "io-gateway";
    public string BindHost { get; init; } = NetworkAddressDefaults.DefaultBindHost;
    public int Port { get; init; } = 12074;
    public string? AdvertiseHost { get; init; }
    public int? AdvertisePort { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record BasicsRuntimeOutputVector(Guid BrainId, ulong TickId, IReadOnlyList<float> Values);

public interface IBasicsRuntimeClient : IAsyncDisposable
{
    Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default);

    Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default);

    Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task<SpawnBrainViaIOAck?> SpawnBrainAsync(SpawnBrain request, CancellationToken cancellationToken = default);

    Task SubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task UnsubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task SendInputVectorAsync(Guid brainId, IReadOnlyList<float> values, CancellationToken cancellationToken = default);

    void ResetOutputBuffer(Guid brainId);

    Task<BasicsRuntimeOutputVector?> WaitForOutputVectorAsync(
        Guid brainId,
        ulong afterTickExclusive,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<KillBrainViaIOAck?> KillBrainAsync(Guid brainId, string reason, CancellationToken cancellationToken = default);

    Task<Nbn.Proto.Repro.ReproduceResult?> ReproduceByArtifactsAsync(
        ReproduceByArtifactsRequest request,
        CancellationToken cancellationToken = default);

    Task<SpeciationAssignResponse?> AssignSpeciationAsync(
        SpeciationAssignRequest request,
        CancellationToken cancellationToken = default);

    Task<SpeciationGetConfigResponse?> GetSpeciationConfigAsync(CancellationToken cancellationToken = default);

    Task<SpeciationSetConfigResponse?> SetSpeciationConfigAsync(
        SpeciationRuntimeConfig config,
        bool startNewEpoch,
        CancellationToken cancellationToken = default);
}

public sealed class BasicsRuntimeClient : IBasicsRuntimeClient, IBasicsRuntimeEventSink
{
    private readonly ActorSystem _system;
    private readonly PID _ioPid;
    private readonly PID _receiverPid;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<Guid, Channel<BasicsRuntimeOutputVector>> _outputBuffers;
    private bool _disposed;

    private BasicsRuntimeClient(
        ActorSystem system,
        PID ioPid,
        PID receiverPid,
        TimeSpan requestTimeout)
    {
        _system = system;
        _ioPid = ioPid;
        _receiverPid = receiverPid;
        _requestTimeout = requestTimeout;
        _outputBuffers = new ConcurrentDictionary<Guid, Channel<BasicsRuntimeOutputVector>>();
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

        var runtimeEventSink = new RuntimeEventSinkProxy();
        var receiverPid = system.Root.SpawnNamed(
            Props.FromProducer(() => new BasicsRuntimeReceiverActor(runtimeEventSink)),
            $"basics-runtime-receiver-{Guid.NewGuid():N}");
        var client = new BasicsRuntimeClient(
            system,
            new PID(options.IoAddress.Trim(), options.IoGatewayName.Trim()),
            receiverPid,
            options.RequestTimeout);
        runtimeEventSink.OnOutput = client.OnOutputVectorEvent;
        system.Root.Send(receiverPid, new BasicsSetIoGatewayPid(client._ioPid));
        return client;
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

    public Task SubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        EnsureOutputBuffer(brainId);
        _system.Root.Send(_receiverPid, new BasicsSubscribeOutputsVectorCommand(brainId));
        return Task.CompletedTask;
    }

    public Task UnsubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        _system.Root.Send(_receiverPid, new BasicsUnsubscribeOutputsVectorCommand(brainId));
        return Task.CompletedTask;
    }

    public Task SendInputVectorAsync(
        Guid brainId,
        IReadOnlyList<float> values,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(values);
        if (brainId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        _system.Root.Send(_receiverPid, new BasicsInputVectorCommand(brainId, values));
        return Task.CompletedTask;
    }

    public void ResetOutputBuffer(Guid brainId)
    {
        if (brainId == Guid.Empty)
        {
            return;
        }

        if (!_outputBuffers.TryGetValue(brainId, out var channel))
        {
            return;
        }

        while (channel.Reader.TryRead(out _))
        {
        }
    }

    public async Task<BasicsRuntimeOutputVector?> WaitForOutputVectorAsync(
        Guid brainId,
        ulong afterTickExclusive,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return null;
        }

        var channel = EnsureOutputBuffer(brainId);
        using var timeoutCts = timeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var effectiveToken = timeoutCts?.Token ?? cancellationToken;
        try
        {
            while (true)
            {
                var output = await channel.Reader.ReadAsync(effectiveToken).ConfigureAwait(false);
                if (output.TickId > afterTickExclusive)
                {
                    return output;
                }
            }
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<KillBrainViaIOAck?> KillBrainAsync(
        Guid brainId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await _system.Root.RequestAsync<KillBrainViaIOAck>(
                    _ioPid,
                    new KillBrainViaIO
                    {
                        Request = new KillBrain
                        {
                            BrainId = brainId.ToProtoUuid(),
                            Reason = reason ?? string.Empty
                        }
                    },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<Nbn.Proto.Repro.ReproduceResult?> ReproduceByArtifactsAsync(
        ReproduceByArtifactsRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var response = await _system.Root.RequestAsync<Nbn.Proto.Io.ReproduceResult>(
                    _ioPid,
                    new ReproduceByArtifacts { Request = request },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return response?.Result;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SpeciationAssignResponse?> AssignSpeciationAsync(
        SpeciationAssignRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var response = await _system.Root.RequestAsync<SpeciationAssignResult>(
                    _ioPid,
                    new SpeciationAssign { Request = request },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return response?.Response;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SpeciationGetConfigResponse?> GetSpeciationConfigAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            var response = await _system.Root.RequestAsync<SpeciationGetConfigResult>(
                    _ioPid,
                    new SpeciationGetConfig { Request = new SpeciationGetConfigRequest() },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return response?.Response;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SpeciationSetConfigResponse?> SetSpeciationConfigAsync(
        SpeciationRuntimeConfig config,
        bool startNewEpoch,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var response = await _system.Root.RequestAsync<SpeciationSetConfigResult>(
                    _ioPid,
                    new SpeciationSetConfig
                    {
                        Request = new SpeciationSetConfigRequest
                        {
                            Config = config.Clone(),
                            StartNewEpoch = startNewEpoch
                        }
                    },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return response?.Response;
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
        foreach (var channel in _outputBuffers.Values)
        {
            channel.Writer.TryComplete();
        }

        await _system.Remote().ShutdownAsync(graceful: true).ConfigureAwait(false);
        await _system.ShutdownAsync().ConfigureAwait(false);
    }

    void IBasicsRuntimeEventSink.OnOutputVectorEvent(OutputVectorEvent output)
    {
        OnOutputVectorEvent(output);
    }

    private void OnOutputVectorEvent(OutputVectorEvent output)
    {
        if (output.BrainId?.TryToGuid(out var brainId) != true || brainId == Guid.Empty)
        {
            return;
        }

        var channel = EnsureOutputBuffer(brainId);
        var values = output.Values.Count == 0
            ? Array.Empty<float>()
            : output.Values.ToArray();
        channel.Writer.TryWrite(new BasicsRuntimeOutputVector(brainId, output.TickId, values));
    }

    private Channel<BasicsRuntimeOutputVector> EnsureOutputBuffer(Guid brainId)
    {
        return _outputBuffers.GetOrAdd(
            brainId,
            static _ => Channel.CreateUnbounded<BasicsRuntimeOutputVector>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false
                }));
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

    private sealed class RuntimeEventSinkProxy : IBasicsRuntimeEventSink
    {
        public Action<OutputVectorEvent>? OnOutput { get; set; }

        public void OnOutputVectorEvent(OutputVectorEvent output)
        {
            OnOutput?.Invoke(output);
        }
    }
}
