using System.Text.Json;
using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Harness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            if (IsHelpToken(args[0]))
            {
                PrintUsage();
                return 0;
            }

            if (string.Equals(args[0], "smoke-local", StringComparison.OrdinalIgnoreCase))
            {
                return await RunLocalSmokeAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            }

            return await RunConfiguredHarnessAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetBaseException().Message);
            return 1;
        }
    }

    private static async Task<int> RunConfiguredHarnessAsync(string[] args)
    {
        var command = ParseHarnessArguments(args);
        if (command.WriteSampleConfigPath is not null)
        {
            await WriteSampleConfigAsync(command.WriteSampleConfigPath).ConfigureAwait(false);
            Console.WriteLine($"Wrote sample config to {command.WriteSampleConfigPath}");
            return 0;
        }

        if (command.ConfigPath is null)
        {
            PrintUsage();
            return 1;
        }

        var config = await LoadConfigAsync(command.ConfigPath).ConfigureAwait(false);
        var resolved = config.Resolve();
        var harness = new BasicsLiveTrialHarness();
        var report = await harness.RunAsync(
                resolved.Options,
                resolved.Plugin,
                PrintProgress,
                CancellationToken.None)
            .ConfigureAwait(false);

        var outputDirectory = command.OutputDirectoryOverride ?? config.OutputDirectory;
        var reportPath = await WriteReportAsync(outputDirectory, report).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine($"Report: {reportPath}");
        Console.WriteLine(
            report.StabilityTargetMet
                ? $"Stable target met after {report.ExecutedTrialCount} trial(s)."
                : $"Stable target not met after {report.ExecutedTrialCount} trial(s).");

        return report.StabilityTargetMet ? 0 : 2;
    }

    private static async Task<int> RunLocalSmokeAsync(string[] args)
    {
        var command = ParseHarnessArguments(args);
        if (command.WriteSampleConfigPath is not null)
        {
            throw new InvalidOperationException("--write-sample-config is not valid with smoke-local.");
        }

        var config = command.ConfigPath is null
            ? CreateLocalSmokeConfig(command.OutputDirectoryOverride)
            : await LoadConfigAsync(command.ConfigPath).ConfigureAwait(false);
        var resolved = config.Resolve();
        var ioPort = ResolveIoPort(resolved.Options.RuntimeClient.IoAddress);

        await using var runtimeHost = await LocalSmokeRuntimeHost.StartAsync(
                new LocalSmokeRuntimeHostOptions
                {
                    IoPort = ioPort
                })
            .ConfigureAwait(false);

        var smokeOptions = resolved.Options with
        {
            RunLabel = $"{resolved.Options.RunLabel}-local-smoke",
            RuntimeClient = resolved.Options.RuntimeClient with
            {
                IoAddress = runtimeHost.IoAddress,
                IoGatewayName = runtimeHost.IoGatewayName
            },
            TemplatePublishing = resolved.Options.TemplatePublishing with
            {
                BackingStoreRoot = runtimeHost.ArtifactRoot
            },
            MaxTrialCount = 1,
            TrialTimeout = TimeSpan.FromSeconds(Math.Min(10d, Math.Max(1d, resolved.Options.TrialTimeout.TotalSeconds))),
            StabilityCriteria = resolved.Options.StabilityCriteria with
            {
                RequiredSuccessfulTrials = 1
            },
            AutoTuning = resolved.Options.AutoTuning with
            {
                Enabled = false
            }
        };

        var smokeClient = await BasicsRuntimeClient.StartAsync(smokeOptions.RuntimeClient).ConfigureAwait(false);
        await ConnectHarnessClientWithRetryAsync(
                smokeClient,
                smokeOptions.Environment.ClientName,
                TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);

        using var loggingOverrides = ApplySmokeLoggingOverrides();
        var harness = new BasicsLiveTrialHarness(
            runtimeClientFactory: (_, _) => Task.FromResult<IBasicsRuntimeClient>(smokeClient),
            executionRunnerFactory: null);
        var report = await harness.RunAsync(
                smokeOptions,
                resolved.Plugin,
                PrintProgress,
                CancellationToken.None)
            .ConfigureAwait(false);

        var outputDirectory = command.OutputDirectoryOverride ?? config.OutputDirectory;
        var reportPath = await WriteReportAsync(outputDirectory, report).ConfigureAwait(false);
        var smokePassed = IsSmokeSuccess(report);

        Console.WriteLine();
        Console.WriteLine($"Report: {reportPath}");
        Console.WriteLine(smokePassed
            ? "Local smoke passed: the in-process NBN stack reached live harness execution and emitted trial telemetry."
            : "Local smoke failed: the in-process NBN stack did not reach live harness execution cleanly.");

        return smokePassed ? 0 : 2;
    }

    private static HarnessCommandArguments ParseHarnessArguments(string[] args)
    {
        string? configPath = null;
        string? writeSampleConfigPath = null;
        string? outputDirectory = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--config":
                    configPath = RequireValue(args, ++index, "--config");
                    break;
                case "--write-sample-config":
                    writeSampleConfigPath = RequireValue(args, ++index, "--write-sample-config");
                    break;
                case "--output-dir":
                    outputDirectory = RequireValue(args, ++index, "--output-dir");
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    System.Environment.Exit(0);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument '{arg}'.");
            }
        }

        return new HarnessCommandArguments(configPath, writeSampleConfigPath, outputDirectory);
    }

    private static string RequireValue(string[] args, int index, string name)
    {
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new InvalidOperationException($"{name} requires a value.");
        }

        return args[index];
    }

    private static async Task<HarnessFileConfig> LoadConfigAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Harness config file was not found.", fullPath);
        }

        await using var stream = File.OpenRead(fullPath);
        var config = await JsonSerializer.DeserializeAsync(
                stream,
                HarnessJsonContext.Default.HarnessFileConfig)
            .ConfigureAwait(false);
        return config ?? throw new InvalidOperationException($"Harness config '{fullPath}' is empty or invalid.");
    }

    private static async Task WriteSampleConfigAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            HarnessFileConfig.CreateDefault(),
            HarnessJsonContext.Default.HarnessFileConfig);
        await File.WriteAllTextAsync(fullPath, json).ConfigureAwait(false);
    }

    private static async Task<string> WriteReportAsync(string outputDirectory, BasicsLiveTrialReport report)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(outputDirectory) ? "artifacts/live-trials" : outputDirectory);
        Directory.CreateDirectory(root);
        var safeLabel = SanitizeFileComponent(report.RunLabel);
        var fileName = $"{safeLabel}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";
        var fullPath = Path.Combine(root, fileName);
        var json = JsonSerializer.Serialize(report, HarnessJsonContext.Default.BasicsLiveTrialReport);
        await File.WriteAllTextAsync(fullPath, json).ConfigureAwait(false);
        return fullPath;
    }

    private static void PrintProgress(BasicsLiveTrialProgress progress)
    {
        switch (progress.Phase)
        {
            case BasicsLiveTrialPhase.Running when progress.Snapshot is not null:
                Console.WriteLine(
                    $"[trial {progress.TrialNumber}] gen {progress.Snapshot.Generation} {progress.Snapshot.State}: accuracy={progress.Snapshot.BestAccuracy:0.###} fitness={progress.Snapshot.BestFitness:0.###} species={progress.Snapshot.SpeciesCount} :: {progress.Snapshot.StatusText}");
                break;
            case BasicsLiveTrialPhase.Tuning when progress.TuningDecision is not null:
                Console.WriteLine(
                    $"[trial {progress.TrialNumber}] tuning: {progress.TuningDecision.Reason} ({string.Join(", ", progress.TuningDecision.Changes)})");
                break;
            default:
                Console.WriteLine($"[trial {progress.TrialNumber}] {progress.Phase}: {progress.Message}");
                break;
        }
    }

    private static HarnessFileConfig CreateLocalSmokeConfig(string? outputDirectoryOverride)
    {
        var defaults = HarnessFileConfig.CreateDefault();
        return defaults with
        {
            RunLabel = "basics-local-smoke",
            OutputDirectory = outputDirectoryOverride ?? "artifacts/live-trials/local-smoke",
            Environment = defaults.Environment with
            {
                ClientName = "nbn.basics.harness.local-smoke",
                Sizing = defaults.Environment.Sizing with
                {
                    InitialPopulationCount = 8,
                    ReproductionRunCount = 1,
                    MaxConcurrentBrains = 2
                },
                Scheduling = defaults.Environment.Scheduling with
                {
                    MaxParentsPerSpecies = 4,
                    MinRunsPerPair = 1,
                    MaxRunsPerPair = 2
                }
            },
            Trials = defaults.Trials with
            {
                MaxTrialCount = 1,
                TrialTimeoutSeconds = 10,
                RequiredSuccessfulTrials = 1,
                AutoTuneEnabled = false
            }
        };
    }

    private static int ResolveIoPort(string? ioAddress)
    {
        if (string.IsNullOrWhiteSpace(ioAddress))
        {
            return 12050;
        }

        var trimmed = ioAddress.Trim();
        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= trimmed.Length - 1)
        {
            return 12050;
        }

        return int.TryParse(trimmed[(lastColon + 1)..], out var port) && port > 0
            ? port
            : 12050;
    }

    private static bool IsSmokeSuccess(BasicsLiveTrialReport report)
    {
        if (report.Trials.Count == 0)
        {
            return false;
        }

        var terminal = report.Trials[^1];
        if (terminal.Outcome is BasicsLiveTrialOutcome.ConnectFailed
            or BasicsLiveTrialOutcome.PlanningFailed
            or BasicsLiveTrialOutcome.RuntimeClientFailed)
        {
            return false;
        }

        if (terminal.TerminalSnapshot is not null && terminal.TerminalSnapshot.Generation > 0)
        {
            return true;
        }

        return terminal.Snapshots.Any(snapshot => snapshot.Generation > 0);
    }

    private static string SanitizeFileComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "basics-live";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Trim()
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : char.ToLowerInvariant(ch))
            .ToArray();
        return new string(chars);
    }

    private static bool IsHelpToken(string token)
        => token.Equals("--help", StringComparison.OrdinalIgnoreCase)
           || token.Equals("-h", StringComparison.OrdinalIgnoreCase)
           || token.Equals("help", StringComparison.OrdinalIgnoreCase);

    private static IDisposable ApplySmokeLoggingOverrides()
    {
        return new EnvironmentOverrideScope(new Dictionary<string, string?>
        {
            ["Logging__LogLevel__Default"] = "Warning",
            ["Logging__LogLevel__Microsoft"] = "Warning",
            ["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning"
        });
    }

    private static async Task ConnectHarnessClientWithRetryAsync(
        BasicsRuntimeClient client,
        string clientName,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            var ack = await client.ConnectAsync(clientName, timeoutCts.Token).ConfigureAwait(false);
            if (ack is not null)
            {
                return;
            }

            await Task.Delay(200, timeoutCts.Token).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Timed out waiting for the smoke-local IO endpoint to accept remote client connections.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Basics/src/Basics.Harness/Basics.Harness.csproj -- --config <path>");
        Console.WriteLine("  dotnet run --project Basics/src/Basics.Harness/Basics.Harness.csproj -- --write-sample-config <path>");
        Console.WriteLine("  dotnet run --project Basics/src/Basics.Harness/Basics.Harness.csproj -- smoke-local [--config <path>] [--output-dir <path>]");
        Console.WriteLine();
        Console.WriteLine("Optional:");
        Console.WriteLine("  --output-dir <path>   Override the report directory from the config file.");
        Console.WriteLine();
        Console.WriteLine("The smoke-local command boots a temporary in-process NBN stack, waits for IO readiness,");
        Console.WriteLine("runs a reduced Basics harness smoke profile, writes a report, and tears the stack down cleanly.");
    }

    private sealed record HarnessCommandArguments(
        string? ConfigPath,
        string? WriteSampleConfigPath,
        string? OutputDirectoryOverride);

    private sealed class EnvironmentOverrideScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _originalValues;

        public EnvironmentOverrideScope(IReadOnlyDictionary<string, string?> overrides)
        {
            _originalValues = overrides.Keys.ToDictionary(
                static key => key,
                static key => System.Environment.GetEnvironmentVariable(key),
                StringComparer.Ordinal);
            foreach (var pair in overrides)
            {
                System.Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _originalValues)
            {
                System.Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
