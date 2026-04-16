using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;
using Nbn.Proto;

namespace Nbn.Demos.Basics.Ui.Tests;

public sealed class MainWindowViewModelWorkerLaunchTests
{
    [Fact]
    public async Task StartWorkersCommand_BuildsSharedPortLaunchRequest_FromUiFields()
    {
        var workerService = new RecordingWorkerProcessService();
        var viewModel = CreateViewModel(workerService);
        viewModel.BindHost = "127.0.0.1";
        viewModel.AdvertiseHost = "127.0.0.1";
        viewModel.WorkerCountText = "3";
        viewModel.WorkerBasePortText = "12041";
        viewModel.WorkerStoragePctText = "87";
        viewModel.OptionalSettingsAddress = "127.0.0.1:12010";
        viewModel.OptionalSettingsActorName = "SettingsMonitor";

        viewModel.StartWorkersCommand.Execute(null);

        await WaitForAsync(() => workerService.StartCallCount == 1);
        Assert.NotNull(workerService.LastStartRequest);
        var request = workerService.LastStartRequest!;
        Assert.Equal(3, request.WorkerCount);
        Assert.Equal(12041, request.BasePort);
        Assert.Equal(87, request.StoragePercent);
        Assert.Equal("127.0.0.1", request.BindHost);
        Assert.Equal("127.0.0.1", request.AdvertiseHost);
        Assert.Equal("127.0.0.1", request.SettingsHost);
        Assert.Equal(12010, request.SettingsPort);
        Assert.Equal("SettingsMonitor", request.SettingsName);
        await WaitForAsync(() => string.Equals(viewModel.WorkerLauncherStatus, "Started 3 worker(s).", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartWorkersCommand_BlocksInvalidWorkerPort_WithoutCallingWorkerService()
    {
        var workerService = new RecordingWorkerProcessService();
        var viewModel = CreateViewModel(workerService);
        viewModel.WorkerCountText = "2";
        viewModel.WorkerBasePortText = "0";

        viewModel.StartWorkersCommand.Execute(null);

        await WaitForAsync(() => string.Equals(viewModel.WorkerLauncherStatus, "Worker launch blocked.", StringComparison.Ordinal));
        Assert.Equal("Worker port must be an integer between 1 and 65535.", viewModel.WorkerLauncherDetail);
        Assert.Equal(0, workerService.StartCallCount);
    }

    private static MainWindowViewModel CreateViewModel(IBasicsLocalWorkerProcessService workerService)
        => new(
            new UiDispatcher(),
            new StubArtifactExportService(),
            new StubBrainImportService(),
            workerService);

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Timed out waiting for condition.");
    }

    private sealed class RecordingWorkerProcessService : IBasicsLocalWorkerProcessService
    {
        public int LaunchedWorkerCount { get; private set; }

        public int StartCallCount { get; private set; }

        public BasicsLocalWorkerLaunchRequest? LastStartRequest { get; private set; }

        public Task<BasicsLocalWorkerLaunchResult> StartWorkersAsync(
            BasicsLocalWorkerLaunchRequest request,
            CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            LastStartRequest = request;
            LaunchedWorkerCount = request.WorkerCount;
            var workers = LocalWorkerProcessService.BuildWorkerProcessLaunchPlan(
                    ordinal: 1,
                    port: request.BasePort,
                    workerCount: request.WorkerCount,
                    bindHost: request.BindHost,
                    advertiseHost: request.AdvertiseHost,
                    processId: 1234,
                    logRoot: Path.GetTempPath())
                .Workers;
            return Task.FromResult(new BasicsLocalWorkerLaunchResult(
                Success: true,
                StartedCount: workers.Count,
                StartedWorkers: workers,
                StatusText: $"Started {workers.Count} worker(s).",
                DetailText: "test workers started"));
        }

        public Task<BasicsLocalWorkerStopResult> StopLaunchedWorkersAsync(CancellationToken cancellationToken = default)
        {
            var count = LaunchedWorkerCount;
            LaunchedWorkerCount = 0;
            return Task.FromResult(new BasicsLocalWorkerStopResult(count, $"Stopped {count} launched worker(s).", "test workers stopped"));
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

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
}
