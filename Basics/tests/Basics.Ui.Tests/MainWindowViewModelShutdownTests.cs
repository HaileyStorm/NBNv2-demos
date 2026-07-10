using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Google.Protobuf;
using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;
using Nbn.Proto.Control;
using Nbn.Proto.Io;
using Nbn.Shared;
using Proto;

namespace Nbn.Demos.Basics.Ui.Tests;

public sealed class MainWindowViewModelShutdownTests
{
    [Fact]
    public async Task ShutdownAsync_CancelsRun_AndWaitsForExecutionCleanup()
    {
        var executionCts = new CancellationTokenSource();
        var executionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainWindowViewModel(
            new UiDispatcher(),
            new StubArtifactExportService(),
            new StubBrainImportService(),
            new RecordingWorkerProcessService());
        SetPrivateField(viewModel, "_executionCts", executionCts);
        SetPrivateField(viewModel, "_executionCompletion", executionCompletion);

        var shutdown = viewModel.ShutdownAsync();

        Assert.True(executionCts.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        executionCompletion.SetResult();
        await shutdown;
    }

    [Fact]
    public async Task ShutdownAsync_CancelsLifetimeBeforeExecutionTokenIsPublished()
    {
        var executionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new MainWindowViewModel(
            new UiDispatcher(),
            new StubArtifactExportService(),
            new StubBrainImportService(),
            new RecordingWorkerProcessService());
        SetPrivateField(viewModel, "_executionCompletion", executionCompletion);

        var shutdown = viewModel.ShutdownAsync();

        var shutdownCts = GetPrivateField<CancellationTokenSource>(viewModel, "_shutdownCts");
        Assert.True(shutdownCts.IsCancellationRequested);
        using var laterExecutionCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        Assert.True(laterExecutionCts.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        executionCompletion.SetResult();
        await shutdown;
    }

    [Fact]
    public async Task ShutdownAsync_ReleasesRetainedWinner_DisposesRuntimeClient_AndIsIdempotent()
    {
        var port = GetFreeTcpPort();
        var brainId = Guid.NewGuid();
        var options = new BasicsRuntimeClientOptions
        {
            IoAddress = $"127.0.0.1:{port}",
            IoGatewayName = "io-gateway",
            BindHost = "127.0.0.1",
            Port = port,
            RequestTimeout = TimeSpan.FromSeconds(2)
        };

        var runtimeClient = await BasicsRuntimeClient.StartAsync(options);
        var system = GetPrivateField<ActorSystem>(runtimeClient, "_system");
        var receiverPid = GetPrivateField<PID>(runtimeClient, "_receiverPid");
        var observedKill = new TaskCompletionSource<KillBrainViaIO>(TaskCreationOptions.RunContinuationsAsynchronously);
        system.Root.SpawnNamed(
            Props.FromProducer(() => new KillBrainProbeActor(system, receiverPid, observedKill)),
            "io-gateway");

        var workerService = new RecordingWorkerProcessService();
        var viewModel = new MainWindowViewModel(
            new UiDispatcher(),
            new StubArtifactExportService(),
            new StubBrainImportService(),
            workerService);
        SetPrivateField(viewModel, "_runtimeClient", runtimeClient);
        var winner = new BasicsExecutionBestCandidateSummary(
            DefinitionArtifact: new Nbn.Proto.ArtifactRef
            {
                Sha256 = new Nbn.Proto.Sha256 { Value = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 32).ToArray()) },
                MediaType = "application/x-nbn",
                SizeBytes = 1
            },
            SnapshotArtifact: null,
            ActiveBrainId: brainId,
            SpeciesId: "species.test",
            Accuracy: 1f,
            Fitness: 1f,
            Complexity: null,
            ScoreBreakdown: new Dictionary<string, float>(),
            Diagnostics: Array.Empty<string>());
        InvokePrivate(
            viewModel,
            "CaptureTerminalWinnerOwnership",
            winner,
            BasicsExecutionState.Stopped);

        await Task.WhenAll(viewModel.ShutdownAsync(), viewModel.ShutdownAsync());

        var kill = await observedKill.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(kill.Request?.BrainId);
        Assert.True(kill.Request!.BrainId.TryToGuid(out var observedBrainId));
        Assert.Equal(brainId, observedBrainId);
        Assert.Equal("basics_ui_window_close", kill.Request!.Reason);
        Assert.Null(GetPrivateFieldValue(viewModel, "_runtimeClient"));

        InvokePrivate(viewModel, "ApplyWinnerArtifacts", winner, true);
        Assert.Equal(Guid.Empty, Assert.IsType<Guid>(GetPrivateFieldValue(viewModel, "_retainedWinnerBrainId")));

        await using var reboundClient = await BasicsRuntimeClient.StartAsync(options);
        Assert.NotNull(reboundClient);
    }

    private static object? GetPrivateFieldValue(object instance, string fieldName)
        => instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);

    private static T GetPrivateField<T>(object instance, string fieldName) where T : class
        => Assert.IsType<T>(GetPrivateFieldValue(instance, fieldName));

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static object? InvokePrivate(object instance, string methodName, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(instance, arguments);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class KillBrainProbeActor : IActor
    {
        private readonly ActorSystem _system;
        private readonly PID _receiverPid;
        private readonly TaskCompletionSource<KillBrainViaIO> _observedKill;

        public KillBrainProbeActor(
            ActorSystem system,
            PID receiverPid,
            TaskCompletionSource<KillBrainViaIO> observedKill)
        {
            _system = system;
            _receiverPid = receiverPid;
            _observedKill = observedKill;
        }

        public Task ReceiveAsync(IContext context)
        {
            if (context.Message is not KillBrainViaIO kill)
            {
                return Task.CompletedTask;
            }

            _observedKill.TrySetResult(kill.Clone());
            context.Respond(new KillBrainViaIOAck { Accepted = true });
            _system.Root.Send(_receiverPid, new BrainTerminated
            {
                BrainId = kill.Request?.BrainId?.Clone(),
                Reason = "test shutdown"
            });
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkerProcessService : IBasicsLocalWorkerProcessService
    {
        public int LaunchedWorkerCount => 0;

        public Task<BasicsLocalWorkerLaunchResult> StartWorkersAsync(
            BasicsLocalWorkerLaunchRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BasicsLocalWorkerStopResult> StopLaunchedWorkersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BasicsLocalWorkerStopResult(0, "none", "none"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubArtifactExportService : IBasicsArtifactExportService
    {
        public Task<string?> ExportAsync(
            Nbn.Proto.ArtifactRef artifact,
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
}
