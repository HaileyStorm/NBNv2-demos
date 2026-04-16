using System.Diagnostics;
using System.Net.NetworkInformation;
using Nbn.Shared;

namespace Nbn.Demos.Basics.Ui.Services;

public sealed record BasicsLocalWorkerLaunchRequest(
    int WorkerCount,
    int BasePort,
    int StoragePercent,
    string BindHost,
    string AdvertiseHost,
    string SettingsHost,
    int SettingsPort,
    string SettingsName);

public sealed record BasicsLocalWorkerInfo(
    int Port,
    string RootActorName,
    string LogicalName,
    string BindHost,
    string AdvertiseHost,
    int ProcessId,
    string LogPath);

public sealed record BasicsLocalWorkerLaunchResult(
    bool Success,
    int StartedCount,
    IReadOnlyList<BasicsLocalWorkerInfo> StartedWorkers,
    string StatusText,
    string DetailText);

public sealed record BasicsLocalWorkerStopResult(
    int StoppedCount,
    string StatusText,
    string DetailText);

internal sealed record BasicsWorkerProcessLaunchPlan(
    int WorkerCount,
    int Port,
    string RootActorName,
    string LogicalName,
    string LogPath,
    IReadOnlyList<BasicsLocalWorkerInfo> Workers);

public interface IBasicsLocalWorkerProcessService : IAsyncDisposable
{
    int LaunchedWorkerCount { get; }

    Task<BasicsLocalWorkerLaunchResult> StartWorkersAsync(
        BasicsLocalWorkerLaunchRequest request,
        CancellationToken cancellationToken = default);

    Task<BasicsLocalWorkerStopResult> StopLaunchedWorkersAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalWorkerProcessService : IBasicsLocalWorkerProcessService
{
    private static readonly TimeSpan WorkerStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WorkerShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkerStartupPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WorkerPostBindStabilityDelay = TimeSpan.FromMilliseconds(750);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<ManagedWorkerProcess> _processes = new();
    private readonly string _workspaceRoot;
    private readonly string _basicsRoot;
    private readonly string _workerProjectPath;
    private readonly string _workerAssemblyPath;
    private readonly string _workerLogRoot;
    private readonly EventHandler _processExitHandler;
    private int _nextWorkerOrdinal = 1;
    private bool _workerRuntimeBuilt;
    private bool _disposed;

    public LocalWorkerProcessService()
    {
        (_workspaceRoot, _basicsRoot) = ResolveWorkspaceRoots();
        _workerProjectPath = Path.GetFullPath(Path.Combine(_workspaceRoot, "..", "NBNv2", "src", "Nbn.Runtime.WorkerNode", "Nbn.Runtime.WorkerNode.csproj"));
        _workerAssemblyPath = Path.GetFullPath(Path.Combine(_workspaceRoot, "..", "NBNv2", "src", "Nbn.Runtime.WorkerNode", "bin", "Release", "net8.0", "Nbn.Runtime.WorkerNode.dll"));
        _workerLogRoot = Path.Combine(Path.GetTempPath(), "nbn-basics-ui-workers");
        Directory.CreateDirectory(_workerLogRoot);
        _processExitHandler = (_, _) => StopLaunchedWorkersOnProcessExit();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    public int LaunchedWorkerCount
    {
        get
        {
            lock (_processes)
            {
                return _processes.Sum(static process => process.WorkerCount);
            }
        }
    }

    public async Task<BasicsLocalWorkerLaunchResult> StartWorkersAsync(
        BasicsLocalWorkerLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.WorkerCount <= 0)
            {
                return new BasicsLocalWorkerLaunchResult(
                    Success: false,
                    StartedCount: 0,
                    StartedWorkers: Array.Empty<BasicsLocalWorkerInfo>(),
                    StatusText: "Worker launch blocked.",
                    DetailText: "Worker count must be greater than zero.");
            }

            if (request.BasePort <= 0 || request.BasePort > 65535)
            {
                return new BasicsLocalWorkerLaunchResult(
                    Success: false,
                    StartedCount: 0,
                    StartedWorkers: Array.Empty<BasicsLocalWorkerInfo>(),
                    StatusText: "Worker launch blocked.",
                    DetailText: "Worker port must be between 1 and 65535.");
            }

            await EnsureWorkerRuntimeBuiltAsync(cancellationToken).ConfigureAwait(false);

            var ordinal = _nextWorkerOrdinal++;
            var port = FindNextAvailablePort(request.BasePort);
            var plan = BuildWorkerProcessLaunchPlan(
                ordinal,
                port,
                request.WorkerCount,
                request.BindHost,
                request.AdvertiseHost,
                processId: 0,
                _workerLogRoot);
            var started = await StartWorkerProcessAsync(plan, request, cancellationToken).ConfigureAwait(false);
            if (started is null)
            {
                return new BasicsLocalWorkerLaunchResult(
                    Success: false,
                    StartedCount: 0,
                    StartedWorkers: Array.Empty<BasicsLocalWorkerInfo>(),
                    StatusText: "Worker launch failed.",
                    DetailText: $"No workers started. See log file {plan.LogPath}.");
            }

            return new BasicsLocalWorkerLaunchResult(
                Success: true,
                StartedCount: started.Count,
                StartedWorkers: started,
                StatusText: $"Started {started.Count} worker(s).",
                DetailText: BuildWorkerDetail(started));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<BasicsLocalWorkerStopResult> StopLaunchedWorkersAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return new BasicsLocalWorkerStopResult(
                StoppedCount: 0,
                StatusText: "No launched workers to stop.",
                DetailText: "Basics UI worker process service is already disposed.");
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ManagedWorkerProcess> snapshot;
            lock (_processes)
            {
                snapshot = _processes.ToList();
                _processes.Clear();
            }

            foreach (var process in snapshot)
            {
                await StopManagedWorkerProcessAsync(process, cancellationToken).ConfigureAwait(false);
            }

            var stoppedWorkerCount = snapshot.Sum(static process => process.WorkerCount);
            return new BasicsLocalWorkerStopResult(
                StoppedCount: stoppedWorkerCount,
                StatusText: snapshot.Count == 0 ? "No launched workers to stop." : $"Stopped {stoppedWorkerCount} launched worker(s).",
                DetailText: snapshot.Count == 0
                    ? "Basics UI is not currently tracking any worker processes."
                    : "All WorkerNode host processes started by Basics UI in this session were stopped.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopLaunchedWorkersAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
            _lifecycleGate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LocalWorkerProcessService));
        }
    }

    private async Task EnsureWorkerRuntimeBuiltAsync(CancellationToken cancellationToken)
    {
        if (_workerRuntimeBuilt && File.Exists(_workerAssemblyPath))
        {
            return;
        }

        if (!File.Exists(_workerProjectPath))
        {
            throw new FileNotFoundException($"WorkerNode project not found at {_workerProjectPath}.");
        }

        var build = await RunDotnetCommandAsync(
                startInfo =>
                {
                    startInfo.ArgumentList.Add("build");
                    startInfo.ArgumentList.Add(_workerProjectPath);
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add("Release");
                    startInfo.ArgumentList.Add("--disable-build-servers");
                    startInfo.ArgumentList.Add("--nologo");
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (build.ExitCode != 0 || !File.Exists(_workerAssemblyPath))
        {
            throw new InvalidOperationException(
                $"Worker runtime build failed. {TrimOutputTail(build.CombinedOutput)}");
        }

        _workerRuntimeBuilt = true;
    }

    internal static BasicsWorkerProcessLaunchPlan BuildWorkerProcessLaunchPlan(
        int ordinal,
        int port,
        int workerCount,
        string bindHost,
        string advertiseHost,
        int processId,
        string logRoot)
    {
        var rootActorName = $"worker-node-ui-{ordinal}";
        var logicalName = $"nbn.worker.ui.{ordinal}";
        var logPath = Path.Combine(logRoot, $"worker-{ordinal:D2}-{port}.log");
        var workers = Enumerable.Range(0, workerCount)
            .Select(index => new BasicsLocalWorkerInfo(
                Port: port,
                RootActorName: ResolveRootActorName(rootActorName, index),
                LogicalName: logicalName,
                BindHost: bindHost,
                AdvertiseHost: advertiseHost,
                ProcessId: processId,
                LogPath: logPath))
            .ToArray();

        return new BasicsWorkerProcessLaunchPlan(
            workerCount,
            port,
            rootActorName,
            logicalName,
            logPath,
            workers);
    }

    private async Task<IReadOnlyList<BasicsLocalWorkerInfo>?> StartWorkerProcessAsync(
        BasicsWorkerProcessLaunchPlan plan,
        BasicsLocalWorkerLaunchRequest request,
        CancellationToken cancellationToken)
    {
        var writer = TextWriter.Synchronized(new StreamWriter(
            new FileStream(plan.LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        });

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _workspaceRoot
            },
            EnableRaisingEvents = true
        };
        AddWorkerProcessArguments(process.StartInfo, _workerAssemblyPath, plan, request);

        var managed = new ManagedWorkerProcess(
            process,
            writer,
            plan.LogPath,
            plan.Port,
            plan.WorkerCount,
            plan.RootActorName,
            plan.LogicalName);
        DataReceivedEventHandler outputHandler = (_, args) => AppendWorkerLog(managed, "stdout", args.Data);
        DataReceivedEventHandler errorHandler = (_, args) => AppendWorkerLog(managed, "stderr", args.Data);
        EventHandler exitHandler = (_, _) => HandleWorkerExit(managed);
        process.OutputDataReceived += outputHandler;
        process.ErrorDataReceived += errorHandler;
        process.Exited += exitHandler;
        managed.AttachHandlers(outputHandler, errorHandler, exitHandler);

        try
        {
            if (!process.Start())
            {
                DisposeManagedWorkerProcess(managed);
                return null;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var started = await WaitForWorkerPortAsync(process, plan.Port, cancellationToken).ConfigureAwait(false);
            if (!started || !await WaitForWorkerStabilityAsync(process, cancellationToken).ConfigureAwait(false))
            {
                await StopManagedWorkerProcessAsync(managed, cancellationToken).ConfigureAwait(false);
                return null;
            }

            lock (_processes)
            {
                _processes.Add(managed);
            }

            return plan.Workers
                .Select(worker => worker with { ProcessId = process.Id })
                .ToArray();
        }
        catch
        {
            await StopManagedWorkerProcessAsync(managed, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> WaitForWorkerPortAsync(Process process, int port, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(WorkerStartupTimeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                return false;
            }

            if (IsPortInUse(port))
            {
                return true;
            }

            await Task.Delay(WorkerStartupPollInterval, timeoutCts.Token).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> WaitForWorkerStabilityAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(WorkerPostBindStabilityDelay, cancellationToken).ConfigureAwait(false);
            return !process.HasExited;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendWorkerLog(ManagedWorkerProcess process, string streamName, string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        lock (process.SyncRoot)
        {
            process.LogWriter.WriteLine($"{DateTimeOffset.UtcNow:O} [{streamName}] {data}");
            process.Tail.Enqueue(data);
            while (process.Tail.Count > 20)
            {
                process.Tail.Dequeue();
            }
        }
    }

    private void HandleWorkerExit(ManagedWorkerProcess process)
    {
        lock (_processes)
        {
            _processes.Remove(process);
        }

        DisposeManagedWorkerProcess(process);
    }

    private async Task StopManagedWorkerProcessAsync(ManagedWorkerProcess process, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.Process.HasExited)
            {
                process.Process.Kill(entireProcessTree: true);
                await process.Process.WaitForExitAsync(cancellationToken).WaitAsync(WorkerShutdownTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort process cleanup only.
        }
        finally
        {
            DisposeManagedWorkerProcess(process);
        }
    }

    private void StopLaunchedWorkersOnProcessExit()
    {
        if (_disposed)
        {
            return;
        }

        List<ManagedWorkerProcess> snapshot;
        lock (_processes)
        {
            snapshot = _processes.ToList();
            _processes.Clear();
        }

        foreach (var process in snapshot)
        {
            try
            {
                if (!process.Process.HasExited)
                {
                    process.Process.Kill(entireProcessTree: true);
                    process.Process.WaitForExit((int)WorkerShutdownTimeout.TotalMilliseconds);
                }
            }
            catch
            {
                // Best-effort process cleanup during app exit only.
            }
            finally
            {
                DisposeManagedWorkerProcess(process);
            }
        }
    }

    private static void DisposeManagedWorkerProcess(ManagedWorkerProcess process)
    {
        lock (process.SyncRoot)
        {
            try
            {
                process.DetachHandlers();
            }
            catch
            {
            }

            try
            {
                process.LogWriter.Dispose();
            }
            catch
            {
            }

            process.Process.Dispose();
        }
    }

    private static async Task<(int ExitCode, string CombinedOutput)> RunDotnetCommandAsync(
        Action<ProcessStartInfo> configureStartInfo,
        CancellationToken cancellationToken)
    {
        var output = new List<string>();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        configureStartInfo(process.StartInfo);

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        output.Add(await stdout.ConfigureAwait(false));
        output.Add(await stderr.ConfigureAwait(false));
        return (process.ExitCode, string.Join(global::System.Environment.NewLine, output.Where(static text => !string.IsNullOrWhiteSpace(text))));
    }

    private int FindNextAvailablePort(int requestedPort)
    {
        var port = requestedPort;
        while (port <= 65535)
        {
            var inUse = IsPortInUse(port);
            if (!inUse)
            {
                lock (_processes)
                {
                    if (_processes.All(process => process.Port != port))
                    {
                        return port;
                    }
                }
            }

            port++;
        }

        throw new InvalidOperationException($"No available worker port found at or above {requestedPort}.");
    }

    internal static void AddWorkerProcessArguments(
        ProcessStartInfo startInfo,
        string workerAssemblyPath,
        BasicsWorkerProcessLaunchPlan plan,
        BasicsLocalWorkerLaunchRequest request)
    {
        startInfo.ArgumentList.Add(workerAssemblyPath);
        startInfo.ArgumentList.Add("--bind-host");
        startInfo.ArgumentList.Add(request.BindHost);
        startInfo.ArgumentList.Add("--advertise-host");
        startInfo.ArgumentList.Add(request.AdvertiseHost);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(plan.Port.ToString());
        startInfo.ArgumentList.Add("--logical-name");
        startInfo.ArgumentList.Add(plan.LogicalName);
        startInfo.ArgumentList.Add("--root-name");
        startInfo.ArgumentList.Add(plan.RootActorName);
        startInfo.ArgumentList.Add("--worker-count");
        startInfo.ArgumentList.Add(plan.WorkerCount.ToString());
        startInfo.ArgumentList.Add("--settings-host");
        startInfo.ArgumentList.Add(request.SettingsHost);
        startInfo.ArgumentList.Add("--settings-port");
        startInfo.ArgumentList.Add(request.SettingsPort.ToString());
        startInfo.ArgumentList.Add("--settings-name");
        startInfo.ArgumentList.Add(request.SettingsName);
        startInfo.ArgumentList.Add("--storage-pct");
        startInfo.ArgumentList.Add(request.StoragePercent.ToString());
    }

    private static bool IsPortInUse(int port)
        => IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    private static string ResolveRootActorName(string baseRootActorName, int workerIndex)
        => workerIndex == 0 ? baseRootActorName : $"{baseRootActorName}-{workerIndex + 1}";

    private static string BuildWorkerDetail(IReadOnlyList<BasicsLocalWorkerInfo> workers)
        => string.Join(
            " ",
            workers.Select(worker =>
                $"{worker.RootActorName} on {worker.AdvertiseHost}:{worker.Port} (bind {worker.BindHost}, pid {worker.ProcessId}, log {worker.LogPath})."));

    private static string TrimOutputTail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No process output was captured.";
        }

        var lines = value
            .Split(global::System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(12);
        return string.Join(" ", lines);
    }

    private static (string WorkspaceRoot, string BasicsRoot) ResolveWorkspaceRoots()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(LocalWorkerProcessService).Assembly.Location) ?? string.Empty;
        foreach (var start in new[] { assemblyDirectory, AppContext.BaseDirectory, global::System.Environment.CurrentDirectory })
        {
            var current = start;
            while (!string.IsNullOrWhiteSpace(current))
            {
                var nestedBasicsRoot = Path.Combine(current, "Basics");
                if (File.Exists(Path.Combine(nestedBasicsRoot, "Basics.sln")))
                {
                    return (current, nestedBasicsRoot);
                }

                if (File.Exists(Path.Combine(current, "Basics.sln")))
                {
                    var basicsRoot = current;
                    var workspaceRoot = Directory.GetParent(basicsRoot)?.FullName;
                    if (!string.IsNullOrWhiteSpace(workspaceRoot))
                    {
                        return (workspaceRoot, basicsRoot);
                    }
                }

                current = Directory.GetParent(current)?.FullName ?? string.Empty;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Basics workspace roots from the current process.");
    }

    private sealed class ManagedWorkerProcess
    {
        public ManagedWorkerProcess(
            Process process,
            TextWriter logWriter,
            string logPath,
            int port,
            int workerCount,
            string rootActorName,
            string logicalName)
        {
            Process = process;
            LogWriter = logWriter;
            LogPath = logPath;
            Port = port;
            WorkerCount = workerCount;
            RootActorName = rootActorName;
            LogicalName = logicalName;
        }

        public Process Process { get; }

        public TextWriter LogWriter { get; }

        public Queue<string> Tail { get; } = new();

        public object SyncRoot { get; } = new();

        public string LogPath { get; }

        public int Port { get; }

        public int WorkerCount { get; }

        public string RootActorName { get; }

        public string LogicalName { get; }

        private DataReceivedEventHandler? OutputHandler { get; set; }

        private DataReceivedEventHandler? ErrorHandler { get; set; }

        private EventHandler? ExitHandler { get; set; }

        public void AttachHandlers(
            DataReceivedEventHandler outputHandler,
            DataReceivedEventHandler errorHandler,
            EventHandler exitHandler)
        {
            OutputHandler = outputHandler;
            ErrorHandler = errorHandler;
            ExitHandler = exitHandler;
        }

        public void DetachHandlers()
        {
            if (OutputHandler is not null)
            {
                Process.OutputDataReceived -= OutputHandler;
                OutputHandler = null;
            }

            if (ErrorHandler is not null)
            {
                Process.ErrorDataReceived -= ErrorHandler;
                ErrorHandler = null;
            }

            if (ExitHandler is not null)
            {
                Process.Exited -= ExitHandler;
                ExitHandler = null;
            }
        }
    }
}
