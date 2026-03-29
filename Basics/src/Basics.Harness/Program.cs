using System.Text.Json;
using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Harness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = ParseArguments(args);
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
                    progress => PrintProgress(progress),
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
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetBaseException().Message);
            return 1;
        }
    }

    private static CommandArguments ParseArguments(string[] args)
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

        return new CommandArguments(configPath, writeSampleConfigPath, outputDirectory);
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

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Basics/src/Basics.Harness/Basics.Harness.csproj -- --config <path>");
        Console.WriteLine("  dotnet run --project Basics/src/Basics.Harness/Basics.Harness.csproj -- --write-sample-config <path>");
        Console.WriteLine("Optional:");
        Console.WriteLine("  --output-dir <path>   Override the report directory from the config file.");
    }

    private sealed record CommandArguments(
        string? ConfigPath,
        string? WriteSampleConfigPath,
        string? OutputDirectoryOverride);
}
