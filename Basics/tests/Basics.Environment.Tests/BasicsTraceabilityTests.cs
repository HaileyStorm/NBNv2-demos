using System.Diagnostics;
using System.Reflection;
using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsTraceabilityTests
{
    [Fact]
    public void BuildPlanTrace_CapturesEvaluationAndTaskSettings()
    {
        var plan = new BasicsEnvironmentPlan(
            SelectedTask: new BasicsTaskContract(
                TaskId: "multiplication",
                DisplayName: "Multiplication",
                InputWidth: BasicsIoGeometry.InputWidth,
                OutputWidth: BasicsIoGeometry.OutputWidth,
                UsesTickAlignedEvaluation: true,
                Description: "Bounded scalar multiplication."),
            SeedTemplate: new BasicsSeedTemplateContract
            {
                TemplateId = "trace-template",
                Description = "Traceability template"
            },
            SizingOverrides: new BasicsSizingOverrides
            {
                InitialPopulationCount = 48,
                MaxConcurrentBrains = 12
            },
            InitialBrainSeeds:
            [
                new BasicsInitialBrainSeed(
                    DisplayName: "seed-a",
                    DefinitionBytes: [0x01, 0x02, 0x03],
                    DuplicateForReproduction: true,
                    Complexity: new BasicsDefinitionComplexitySummary(1, 3, 7))
                {
                    ContentHash = "abc123",
                    SnapshotBytes = [0x10, 0x20]
                }
            ],
            Capacity: new BasicsCapacityRecommendation(
                Source: BasicsCapacitySource.RuntimePlacementInventory,
                EligibleWorkerCount: 32,
                RecommendedInitialPopulationCount: 48,
                RecommendedReproductionRunCount: 3,
                RecommendedMaxConcurrentBrains: 12,
                CapacityScore: 1.5f,
                EffectiveRamFreeBytes: 16UL * 1024UL * 1024UL * 1024UL,
                Summary: "eligible=32"),
            OutputObservationMode: BasicsOutputObservationMode.EventedOutput,
            OutputSamplingPolicy: new BasicsOutputSamplingPolicy
            {
                MaxReadyWindowTicks = 5,
                SampleRepeatCount = 3,
                VectorReadyThreshold = 0.75f
            },
            DiversityPreset: BasicsDiversityPreset.Low,
            AdaptiveDiversity: new BasicsAdaptiveDiversityOptions
            {
                Enabled = true,
                StallGenerationWindow = 250
            },
            Reproduction: new BasicsReproductionPolicy
            {
                StrengthSource = Nbn.Proto.Repro.StrengthSource.StrengthBaseOnly
            },
            Scheduling: new BasicsReproductionSchedulingPolicy
            {
                ParentSelection = new BasicsParentSelectionPolicy
                {
                    FitnessWeight = 0.65d,
                    DiversityWeight = 0.2d,
                    SpeciesBalanceWeight = 0.15d,
                    EliteFraction = 0.1d,
                    ExplorationFraction = 0.25d,
                    MaxParentsPerSpecies = 5
                },
                RunAllocation = new BasicsRunAllocationPolicy
                {
                    MinRunsPerPair = 2,
                    MaxRunsPerPair = 9,
                    FitnessExponent = 1.35d,
                    DiversityBoost = 0.45d
                }
            },
            Metrics: BasicsMetricsContract.Default,
            StopCriteria: new BasicsExecutionStopCriteria
            {
                TargetAccuracy = 0.95f,
                TargetFitness = 0.97f,
                RequireBothTargets = true,
                MaximumGenerations = 500
            },
            PlannedAtUtc: new DateTimeOffset(2026, 4, 8, 20, 0, 0, TimeSpan.Zero),
            TaskSettings: new BasicsTaskSettings
            {
                Multiplication = new BasicsMultiplicationTaskSettings
                {
                    UniqueInputValueCount = 7,
                    AccuracyTolerance = 0.02f,
                    BehaviorOccupancyEnabled = false,
                    BehaviorStageGateStart = 0.33f,
                    BehaviorStageGateFull = 0.55f
                }
            });

        var trace = BasicsTraceability.BuildPlanTrace(plan);

        Assert.Equal(BasicsTraceability.SchemaVersion, trace.SchemaVersion);
        Assert.Equal("multiplication", trace.Task.TaskId);
        Assert.Equal("Multiplication", trace.Task.DisplayName);
        Assert.Equal("EventedOutput", trace.OutputObservationMode);
        Assert.Equal(5, trace.OutputSamplingPolicy.MaxReadyWindowTicks);
        Assert.Equal(3, trace.OutputSamplingPolicy.SampleRepeatCount);
        Assert.Equal(0.75f, trace.OutputSamplingPolicy.VectorReadyThreshold);
        Assert.Equal("Low", trace.DiversityPreset);
        Assert.True(trace.AdaptiveDiversity.Enabled);
        Assert.Equal(250, trace.AdaptiveDiversity.StallGenerationWindow);
        Assert.Equal(2, trace.VariationBand.MaxInternalNeuronDelta);
        Assert.Equal(8, trace.VariationBand.MaxAxonDelta);
        Assert.True(trace.VariationBand.AllowAxonReroute);
        Assert.Equal(7, trace.TaskSettings.Multiplication.UniqueInputValueCount);
        Assert.Equal(0.02f, trace.TaskSettings.Multiplication.AccuracyTolerance);
        Assert.False(trace.TaskSettings.Multiplication.BehaviorOccupancyEnabled);
        Assert.Equal(0.33f, trace.TaskSettings.Multiplication.BehaviorStageGateStart);
        Assert.Equal(0.55f, trace.TaskSettings.Multiplication.BehaviorStageGateFull);
        Assert.Equal(48, trace.SizingOverrides.InitialPopulationCount);
        Assert.Equal(12, trace.SizingOverrides.MaxConcurrentBrains);
        Assert.Single(trace.InitialBrainSeeds);
        Assert.True(trace.InitialBrainSeeds[0].HasSnapshotBytes);
        Assert.Equal("abc123", trace.InitialBrainSeeds[0].ContentHash);
    }

    [Fact]
    public void BuildBuildTrace_CapturesAssemblyAndRepositoryIdentity()
    {
        var repoRoot = ResolveDemosRepoRoot();
        var expectedHead = RunGit(repoRoot, "rev-parse", "HEAD");

        var trace = BasicsTraceability.BuildBuildTrace(
            assemblies:
            [
                typeof(BasicsTraceability).Assembly
            ],
            repositories:
            [
                new BasicsTraceabilityRepositoryTarget("NBNv2-demos", repoRoot)
            ]);

        Assert.Equal(BasicsTraceability.SchemaVersion, trace.SchemaVersion);
        Assert.Contains(trace.Assemblies, assembly => assembly.Name == "Nbn.Demos.Basics.Environment");

        var repository = Assert.Single(trace.Repositories);
        Assert.Equal("NBNv2-demos", repository.Name);
        Assert.Equal(Path.GetFullPath(repoRoot), repository.RootPath);
        if (expectedHead is null)
        {
            Assert.False(repository.IsGitRepository);
        }
        else
        {
            Assert.True(repository.IsGitRepository);
            Assert.Equal(expectedHead, repository.HeadCommitSha);
        }
    }

    [Fact]
    public void BuildBuildTrace_TreatsUntrackedFilesAsDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"basics-traceability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            RunGitOrThrow(tempRoot, "init");
            File.WriteAllText(Path.Combine(tempRoot, "note.txt"), "dirty");

            var trace = BasicsTraceability.BuildBuildTrace(
                assemblies:
                [
                    typeof(BasicsTraceability).Assembly
                ],
                repositories:
                [
                    new BasicsTraceabilityRepositoryTarget("temp", tempRoot)
                ]);

            var repository = Assert.Single(trace.Repositories);
            Assert.True(repository.IsGitRepository);
            Assert.True(repository.IsDirty);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string ResolveDemosRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = start;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, ".git")) || Directory.Exists(Path.Combine(current, ".git")))
                {
                    if (Directory.Exists(Path.Combine(current, "Basics")))
                    {
                        return current;
                    }
                }

                current = Directory.GetParent(current)?.FullName ?? string.Empty;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the NBNv2-demos repo root.");
    }

    private static string? RunGit(string workingDirectory, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        if (!process.WaitForExit(3000) || process.ExitCode != 0)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }

            return null;
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    private static void RunGitOrThrow(string workingDirectory, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        if (!process.WaitForExit(3000) || process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        }
    }
}
