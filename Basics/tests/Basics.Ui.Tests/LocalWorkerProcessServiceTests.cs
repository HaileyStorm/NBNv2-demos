using System.Diagnostics;
using Nbn.Demos.Basics.Ui.Services;

namespace Nbn.Demos.Basics.Ui.Tests;

public sealed class LocalWorkerProcessServiceTests
{
    [Fact]
    public void BuildWorkerProcessLaunchPlan_UsesOneSharedPortAndGeneratedRootNames()
    {
        var plan = LocalWorkerProcessService.BuildWorkerProcessLaunchPlan(
            ordinal: 7,
            port: 12041,
            workerCount: 3,
            bindHost: "0.0.0.0",
            advertiseHost: "127.0.0.1",
            processId: 1234,
            logRoot: Path.Combine(Path.GetTempPath(), "nbn-basics-worker-tests"));

        Assert.Equal(3, plan.WorkerCount);
        Assert.Equal(12041, plan.Port);
        Assert.Equal("worker-node-ui-7", plan.RootActorName);
        Assert.Equal("nbn.worker.ui.7", plan.LogicalName);
        Assert.Equal(3, plan.Workers.Count);
        Assert.All(plan.Workers, worker =>
        {
            Assert.Equal(12041, worker.Port);
            Assert.Equal("nbn.worker.ui.7", worker.LogicalName);
            Assert.Equal("0.0.0.0", worker.BindHost);
            Assert.Equal("127.0.0.1", worker.AdvertiseHost);
            Assert.Equal(1234, worker.ProcessId);
            Assert.Equal(plan.LogPath, worker.LogPath);
        });
        Assert.Equal("worker-node-ui-7", plan.Workers[0].RootActorName);
        Assert.Equal("worker-node-ui-7-2", plan.Workers[1].RootActorName);
        Assert.Equal("worker-node-ui-7-3", plan.Workers[2].RootActorName);
    }

    [Fact]
    public void AddWorkerProcessArguments_UsesOnePortAndWorkerCount()
    {
        var request = new BasicsLocalWorkerLaunchRequest(
            WorkerCount: 3,
            BasePort: 12041,
            StoragePercent: 95,
            BindHost: "0.0.0.0",
            AdvertiseHost: "127.0.0.1",
            SettingsHost: "127.0.0.1",
            SettingsPort: 12010,
            SettingsName: "SettingsMonitor");
        var plan = LocalWorkerProcessService.BuildWorkerProcessLaunchPlan(
            ordinal: 2,
            port: 12041,
            workerCount: request.WorkerCount,
            bindHost: request.BindHost,
            advertiseHost: request.AdvertiseHost,
            processId: 1234,
            logRoot: Path.GetTempPath());
        var startInfo = new ProcessStartInfo();

        LocalWorkerProcessService.AddWorkerProcessArguments(startInfo, "/runtime/Nbn.Runtime.WorkerNode.dll", plan, request);

        var args = startInfo.ArgumentList.ToArray();
        Assert.Equal(1, CountOption(args, "--port"));
        Assert.Equal("12041", ValueAfter(args, "--port"));
        Assert.Equal("3", ValueAfter(args, "--worker-count"));
        Assert.Equal("worker-node-ui-2", ValueAfter(args, "--root-name"));
        Assert.Equal("nbn.worker.ui.2", ValueAfter(args, "--logical-name"));
        Assert.DoesNotContain("12042", args);
        Assert.DoesNotContain("12043", args);
    }

    [Fact]
    public async Task StartWorkersAsync_ReturnsBlockedResult_WhenWorkerCountIsZero()
    {
        await using var service = new LocalWorkerProcessService();

        var result = await service.StartWorkersAsync(new BasicsLocalWorkerLaunchRequest(
            WorkerCount: 0,
            BasePort: 12041,
            StoragePercent: 95,
            BindHost: "127.0.0.1",
            AdvertiseHost: "127.0.0.1",
            SettingsHost: "127.0.0.1",
            SettingsPort: 12010,
            SettingsName: "SettingsMonitor"));

        Assert.False(result.Success);
        Assert.Equal(0, result.StartedCount);
        Assert.Empty(result.StartedWorkers);
        Assert.Equal("Worker launch blocked.", result.StatusText);
    }

    [Fact]
    public async Task StartWorkersAsync_ReturnsBlockedResult_WhenBasePortIsOutOfRange()
    {
        await using var service = new LocalWorkerProcessService();

        var result = await service.StartWorkersAsync(new BasicsLocalWorkerLaunchRequest(
            WorkerCount: 2,
            BasePort: 65536,
            StoragePercent: 95,
            BindHost: "127.0.0.1",
            AdvertiseHost: "127.0.0.1",
            SettingsHost: "127.0.0.1",
            SettingsPort: 12010,
            SettingsName: "SettingsMonitor"));

        Assert.False(result.Success);
        Assert.Equal(0, result.StartedCount);
        Assert.Equal("Worker port must be between 1 and 65535.", result.DetailText);
    }

    [Fact]
    public async Task StopLaunchedWorkersAsync_ReturnsNoop_WhenNoWorkersTracked()
    {
        await using var service = new LocalWorkerProcessService();

        var result = await service.StopLaunchedWorkersAsync();

        Assert.Equal(0, result.StoppedCount);
        Assert.Equal("No launched workers to stop.", result.StatusText);
    }

    private static int CountOption(IReadOnlyList<string> args, string option)
        => args.Count(value => string.Equals(value, option, StringComparison.Ordinal));

    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        var index = Array.IndexOf(args.ToArray(), option);
        Assert.True(index >= 0 && index < args.Count - 1, $"Missing value after {option}.");
        return args[index + 1];
    }
}
