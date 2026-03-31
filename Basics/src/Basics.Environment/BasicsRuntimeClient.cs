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

public sealed record BasicsRuntimeOutputEvent(Guid BrainId, uint OutputIndex, ulong TickId, float Value);

public interface IBasicsRuntimeClient : IAsyncDisposable
{
    Task<ConnectAck?> ConnectAsync(string clientName, CancellationToken cancellationToken = default);

    Task<PlacementWorkerInventoryResult?> GetPlacementWorkerInventoryAsync(CancellationToken cancellationToken = default);

    Task<BrainInfo?> RequestBrainInfoAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task<SpawnBrainViaIOAck?> SpawnBrainAsync(SpawnBrain request, CancellationToken cancellationToken = default);

    Task<AwaitSpawnPlacementViaIOAck?> AwaitSpawnPlacementAsync(
        Guid brainId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<BrainDefinitionReady?> ExportBrainDefinitionAsync(
        Guid brainId,
        bool rebaseOverlays,
        CancellationToken cancellationToken = default);

    Task<SnapshotReady?> RequestSnapshotAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task SubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task SubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task UnsubscribeOutputsVectorAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task UnsubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default);

    Task SendInputVectorAsync(Guid brainId, IReadOnlyList<float> values, CancellationToken cancellationToken = default);

    void ResetOutputBuffer(Guid brainId);

    void ResetOutputEventBuffer(Guid brainId);

    Task<BasicsRuntimeOutputVector?> WaitForOutputVectorAsync(
        Guid brainId,
        ulong afterTickExclusive,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<BasicsRuntimeOutputEvent?> WaitForOutputEventAsync(
        Guid brainId,
        ulong afterTickExclusive,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<BrainTerminated?> WaitForBrainTerminatedAsync(
        Guid brainId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<KillBrainViaIOAck?> KillBrainAsync(Guid brainId, string reason, CancellationToken cancellationToken = default);

    Task<Nbn.Proto.Io.SetOutputVectorSourceAck?> SetOutputVectorSourceAsync(
        Nbn.Proto.Control.OutputVectorSource outputVectorSource,
        Guid? brainId = null,
        CancellationToken cancellationToken = default);

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
    private readonly Channel<ConnectAck> _connectAcks;
    private readonly ConcurrentDictionary<Guid, Channel<BasicsRuntimeOutputVector>> _outputBuffers;
    private readonly ConcurrentDictionary<Guid, Channel<BasicsRuntimeOutputEvent>> _outputEventBuffers;
    private readonly ConcurrentDictionary<Guid, int> _outputWidths;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<ulong, PendingOutputVector>> _pendingOutputSegments;
    private readonly ConcurrentDictionary<Guid, Channel<BrainTerminated>> _terminationBuffers;
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
        _connectAcks = Channel.CreateUnbounded<ConnectAck>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        _outputBuffers = new ConcurrentDictionary<Guid, Channel<BasicsRuntimeOutputVector>>();
        _outputEventBuffers = new ConcurrentDictionary<Guid, Channel<BasicsRuntimeOutputEvent>>();
        _outputWidths = new ConcurrentDictionary<Guid, int>();
        _pendingOutputSegments = new ConcurrentDictionary<Guid, ConcurrentDictionary<ulong, PendingOutputVector>>();
        _terminationBuffers = new ConcurrentDictionary<Guid, Channel<BrainTerminated>>();
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
        runtimeEventSink.OnConnect = client.OnConnectAck;
        runtimeEventSink.OnSingleOutput = client.OnOutputEvent;
        runtimeEventSink.OnOutput = client.OnOutputVectorEvent;
        runtimeEventSink.OnOutputSegment = client.OnOutputVectorSegment;
        runtimeEventSink.OnTermination = client.OnBrainTerminated;
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
            ResetConnectAckBuffer();
            _system.Root.Send(_receiverPid, new BasicsConnectCommand(clientName.Trim()));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_requestTimeout);
            return await _connectAcks.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
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
            var info = await _system.Root.RequestAsync<BrainInfo>(
                    _ioPid,
                    new BrainInfoRequest { BrainId = brainId.ToProtoUuid() },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            RememberOutputWidth(brainId, info);
            return info;
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
        catch (OperationCanceledException ex)
        {
            var detail = BuildFailureDetail(ex);
            return new SpawnBrainViaIOAck
            {
                FailureReasonCode = "spawn_request_canceled",
                FailureMessage = detail,
                Ack = new SpawnBrainAck
                {
                    BrainId = Guid.Empty.ToProtoUuid(),
                    FailureReasonCode = "spawn_request_canceled",
                    FailureMessage = detail
                }
            };
        }
        catch (Exception ex)
        {
            var detail = BuildFailureDetail(ex);
            return new SpawnBrainViaIOAck
            {
                FailureReasonCode = "spawn_request_failed",
                FailureMessage = detail,
                Ack = new SpawnBrainAck
                {
                    BrainId = Guid.Empty.ToProtoUuid(),
                    FailureReasonCode = "spawn_request_failed",
                    FailureMessage = detail
                }
            };
        }
    }

    public async Task<AwaitSpawnPlacementViaIOAck?> AwaitSpawnPlacementAsync(
        Guid brainId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return null;
        }

        var timeoutMs = timeout <= TimeSpan.Zero
            ? 0UL
            : checked((ulong)Math.Min(timeout.TotalMilliseconds, ulong.MaxValue));

        try
        {
            return await _system.Root.RequestAsync<AwaitSpawnPlacementViaIOAck>(
                    _ioPid,
                    new AwaitSpawnPlacementViaIO
                    {
                        BrainId = brainId.ToProtoUuid(),
                        TimeoutMs = timeoutMs
                    },
                    timeout > TimeSpan.Zero ? timeout + TimeSpan.FromSeconds(1) : _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            var detail = BuildFailureDetail(ex);
            return new AwaitSpawnPlacementViaIOAck
            {
                FailureReasonCode = "spawn_request_canceled",
                FailureMessage = detail,
                Ack = new SpawnBrainAck
                {
                    BrainId = brainId.ToProtoUuid(),
                    FailureReasonCode = "spawn_request_canceled",
                    FailureMessage = detail
                }
            };
        }
        catch (Exception ex)
        {
            var detail = BuildFailureDetail(ex);
            return new AwaitSpawnPlacementViaIOAck
            {
                FailureReasonCode = "spawn_request_failed",
                FailureMessage = detail,
                Ack = new SpawnBrainAck
                {
                    BrainId = brainId.ToProtoUuid(),
                    FailureReasonCode = "spawn_request_failed",
                    FailureMessage = detail
                }
            };
        }
    }

    public async Task<BrainDefinitionReady?> ExportBrainDefinitionAsync(
        Guid brainId,
        bool rebaseOverlays,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await _system.Root.RequestAsync<BrainDefinitionReady>(
                    _ioPid,
                    new ExportBrainDefinition
                    {
                        BrainId = brainId.ToProtoUuid(),
                        RebaseOverlays = rebaseOverlays
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

    public async Task<SnapshotReady?> RequestSnapshotAsync(Guid brainId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await _system.Root.RequestAsync<SnapshotReady>(
                    _ioPid,
                    new RequestSnapshot
                    {
                        BrainId = brainId.ToProtoUuid()
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

    public Task SubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        EnsureOutputEventBuffer(brainId);
        _system.Root.Send(_receiverPid, new BasicsSubscribeOutputsCommand(brainId));
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

    public Task UnsubscribeOutputsAsync(Guid brainId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        _system.Root.Send(_receiverPid, new BasicsUnsubscribeOutputsCommand(brainId));
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

        _pendingOutputSegments.TryRemove(brainId, out _);
    }

    public void ResetOutputEventBuffer(Guid brainId)
    {
        if (brainId == Guid.Empty)
        {
            return;
        }

        if (!_outputEventBuffers.TryGetValue(brainId, out var channel))
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

    public async Task<BasicsRuntimeOutputEvent?> WaitForOutputEventAsync(
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

        var channel = EnsureOutputEventBuffer(brainId);
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
            ResetTerminationBuffer(brainId);
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

    public async Task<BrainTerminated?> WaitForBrainTerminatedAsync(
        Guid brainId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (brainId == Guid.Empty)
        {
            return null;
        }

        var channel = EnsureTerminationBuffer(brainId);
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
            return await channel.Reader.ReadAsync(effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<Nbn.Proto.Io.SetOutputVectorSourceAck?> SetOutputVectorSourceAsync(
        Nbn.Proto.Control.OutputVectorSource outputVectorSource,
        Guid? brainId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            return await _system.Root.RequestAsync<Nbn.Proto.Io.SetOutputVectorSourceAck>(
                    _ioPid,
                    new Nbn.Proto.Io.SetOutputVectorSource
                    {
                        OutputVectorSource = outputVectorSource,
                        BrainId = brainId.HasValue && brainId.Value != Guid.Empty
                            ? brainId.Value.ToProtoUuid()
                            : null
                    },
                    _requestTimeout)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            var detail = BuildFailureDetail(ex, "Output vector source update request was canceled.");
            return new Nbn.Proto.Io.SetOutputVectorSourceAck
            {
                Success = false,
                FailureReasonCode = "output_vector_source_request_canceled",
                FailureMessage = detail,
                OutputVectorSource = outputVectorSource,
                BrainId = brainId.HasValue && brainId.Value != Guid.Empty
                    ? brainId.Value.ToProtoUuid()
                    : null
            };
        }
        catch (Exception ex)
        {
            var detail = BuildFailureDetail(ex, "Output vector source update request failed.");
            return new Nbn.Proto.Io.SetOutputVectorSourceAck
            {
                Success = false,
                FailureReasonCode = "output_vector_source_request_failed",
                FailureMessage = detail,
                OutputVectorSource = outputVectorSource,
                BrainId = brainId.HasValue && brainId.Value != Guid.Empty
                    ? brainId.Value.ToProtoUuid()
                    : null
            };
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

        foreach (var channel in _outputEventBuffers.Values)
        {
            channel.Writer.TryComplete();
        }

        foreach (var channel in _terminationBuffers.Values)
        {
            channel.Writer.TryComplete();
        }

        _connectAcks.Writer.TryComplete();

        await _system.Remote().ShutdownAsync(graceful: true).ConfigureAwait(false);
        await _system.ShutdownAsync().ConfigureAwait(false);
    }

    void IBasicsRuntimeEventSink.OnConnectAck(ConnectAck ack)
    {
        OnConnectAck(ack);
    }

    void IBasicsRuntimeEventSink.OnOutputEvent(OutputEvent output)
    {
        OnOutputEvent(output);
    }

    void IBasicsRuntimeEventSink.OnOutputVectorEvent(OutputVectorEvent output)
    {
        OnOutputVectorEvent(output);
    }

    void IBasicsRuntimeEventSink.OnOutputVectorSegment(OutputVectorSegment output)
    {
        OnOutputVectorSegment(output);
    }

    void IBasicsRuntimeEventSink.OnBrainTerminated(BrainTerminated terminated)
    {
        OnBrainTerminated(terminated);
    }

    private void OnOutputEvent(OutputEvent output)
    {
        if (output.BrainId?.TryToGuid(out var brainId) != true || brainId == Guid.Empty)
        {
            return;
        }

        var channel = EnsureOutputEventBuffer(brainId);
        channel.Writer.TryWrite(new BasicsRuntimeOutputEvent(brainId, output.OutputIndex, output.TickId, output.Value));
    }

    private void OnConnectAck(ConnectAck ack)
    {
        _connectAcks.Writer.TryWrite(ack);
    }

    private void OnOutputVectorEvent(OutputVectorEvent output)
    {
        if (output.BrainId?.TryToGuid(out var brainId) != true || brainId == Guid.Empty)
        {
            return;
        }

        var values = output.Values.Count == 0
            ? Array.Empty<float>()
            : output.Values.ToArray();
        RememberOutputWidth(brainId, values.Length);
        if (_pendingOutputSegments.TryGetValue(brainId, out var pendingByTick))
        {
            pendingByTick.TryRemove(output.TickId, out _);
        }

        WriteOutputVector(brainId, output.TickId, values);
    }

    private void OnOutputVectorSegment(OutputVectorSegment output)
    {
        if (output.BrainId?.TryToGuid(out var brainId) != true || brainId == Guid.Empty || output.Values.Count == 0)
        {
            return;
        }

        var outputWidth = ResolveOutputWidth(brainId, output);
        if (outputWidth <= 0)
        {
            return;
        }

        if (output.OutputIndexStart > int.MaxValue)
        {
            return;
        }

        var startIndex = checked((int)output.OutputIndexStart);
        if (startIndex < 0 || startIndex >= outputWidth)
        {
            return;
        }

        if ((long)startIndex + output.Values.Count > outputWidth)
        {
            return;
        }

        if (startIndex == 0 && output.Values.Count == outputWidth)
        {
            if (_pendingOutputSegments.TryGetValue(brainId, out var pendingByTick))
            {
                pendingByTick.TryRemove(output.TickId, out _);
            }

            WriteOutputVector(brainId, output.TickId, output.Values.ToArray());
            return;
        }

        var pending = GetOrCreatePendingOutputVector(brainId, output.TickId, outputWidth);
        float[]? completedValues = null;
        lock (pending)
        {
            for (var i = 0; i < output.Values.Count; i++)
            {
                var outputIndex = startIndex + i;
                if (pending.Filled[outputIndex])
                {
                    if (pending.Values[outputIndex] != output.Values[i])
                    {
                        return;
                    }

                    continue;
                }

                pending.Values[outputIndex] = output.Values[i];
                pending.Filled[outputIndex] = true;
                pending.FilledCount++;
            }

            if (pending.FilledCount >= outputWidth)
            {
                completedValues = (float[])pending.Values.Clone();
            }
        }

        if (completedValues is null)
        {
            return;
        }

        if (_pendingOutputSegments.TryGetValue(brainId, out var completedByTick))
        {
            completedByTick.TryRemove(output.TickId, out _);
        }

        WriteOutputVector(brainId, output.TickId, completedValues);
    }

    private void OnBrainTerminated(BrainTerminated terminated)
    {
        if (terminated.BrainId?.TryToGuid(out var brainId) != true || brainId == Guid.Empty)
        {
            return;
        }

        _pendingOutputSegments.TryRemove(brainId, out _);
        _outputWidths.TryRemove(brainId, out _);
        var channel = EnsureTerminationBuffer(brainId);
        channel.Writer.TryWrite(terminated.Clone());
    }

    private void WriteOutputVector(Guid brainId, ulong tickId, IReadOnlyList<float> values)
    {
        var channel = EnsureOutputBuffer(brainId);
        channel.Writer.TryWrite(new BasicsRuntimeOutputVector(brainId, tickId, values));
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

    private Channel<BasicsRuntimeOutputEvent> EnsureOutputEventBuffer(Guid brainId)
    {
        return _outputEventBuffers.GetOrAdd(
            brainId,
            static _ => Channel.CreateUnbounded<BasicsRuntimeOutputEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false
                }));
    }

    private Channel<BrainTerminated> EnsureTerminationBuffer(Guid brainId)
    {
        return _terminationBuffers.GetOrAdd(
            brainId,
            static _ => Channel.CreateUnbounded<BrainTerminated>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false
                }));
    }

    private void ResetTerminationBuffer(Guid brainId)
    {
        if (brainId == Guid.Empty || !_terminationBuffers.TryGetValue(brainId, out var channel))
        {
            return;
        }

        while (channel.Reader.TryRead(out _))
        {
        }
    }

    private void RememberOutputWidth(Guid brainId, BrainInfo? info)
    {
        if (info is null)
        {
            return;
        }

        RememberOutputWidth(brainId, checked((int)info.OutputWidth));
    }

    private void RememberOutputWidth(Guid brainId, int outputWidth)
    {
        if (brainId == Guid.Empty || outputWidth <= 0)
        {
            return;
        }

        _outputWidths[brainId] = outputWidth;
    }

    private int ResolveOutputWidth(Guid brainId, OutputVectorSegment output)
    {
        if (_outputWidths.TryGetValue(brainId, out var outputWidth) && outputWidth > 0)
        {
            return outputWidth;
        }

        if (output.OutputIndexStart == 0 && output.Values.Count > 0)
        {
            outputWidth = output.Values.Count;
            RememberOutputWidth(brainId, outputWidth);
            return outputWidth;
        }

        return 0;
    }

    private PendingOutputVector GetOrCreatePendingOutputVector(Guid brainId, ulong tickId, int outputWidth)
    {
        var pendingByTick = _pendingOutputSegments.GetOrAdd(
            brainId,
            static _ => new ConcurrentDictionary<ulong, PendingOutputVector>());
        return pendingByTick.AddOrUpdate(
            tickId,
            static (_, width) => new PendingOutputVector(width),
            static (_, existing, width) => existing.Values.Length == width ? existing : new PendingOutputVector(width),
            outputWidth);
    }

    private void ResetConnectAckBuffer()
    {
        while (_connectAcks.Reader.TryRead(out _))
        {
        }
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

    private static string BuildFailureDetail(Exception ex, string fallback = "Request forwarding failed.")
    {
        var baseException = ex.GetBaseException();
        var message = string.IsNullOrWhiteSpace(baseException.Message)
            ? fallback
            : baseException.Message.Trim();
        return $"{baseException.GetType().Name}: {message}";
    }

    private sealed class RuntimeEventSinkProxy : IBasicsRuntimeEventSink
    {
        public Action<ConnectAck>? OnConnect { get; set; }
        public Action<OutputEvent>? OnSingleOutput { get; set; }
        public Action<OutputVectorEvent>? OnOutput { get; set; }
        public Action<OutputVectorSegment>? OnOutputSegment { get; set; }
        public Action<BrainTerminated>? OnTermination { get; set; }

        public void OnConnectAck(ConnectAck ack)
        {
            OnConnect?.Invoke(ack);
        }

        public void OnOutputEvent(OutputEvent output)
        {
            OnSingleOutput?.Invoke(output);
        }

        public void OnOutputVectorEvent(OutputVectorEvent output)
        {
            OnOutput?.Invoke(output);
        }

        public void OnOutputVectorSegment(OutputVectorSegment output)
        {
            OnOutputSegment?.Invoke(output);
        }

        public void OnBrainTerminated(BrainTerminated terminated)
        {
            OnTermination?.Invoke(terminated);
        }
    }

    private sealed class PendingOutputVector
    {
        public PendingOutputVector(int outputWidth)
        {
            Values = new float[outputWidth];
            Filled = new bool[outputWidth];
        }

        public float[] Values { get; }

        public bool[] Filled { get; }

        public int FilledCount { get; set; }
    }
}
