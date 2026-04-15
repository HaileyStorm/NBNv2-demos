using System.Diagnostics;
using System.Reflection;
using Nbn.Proto;
using Nbn.Shared;

namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsTraceabilityRepositoryTarget(string Name, string RootPath);

public sealed record BasicsTraceabilityAssemblyRecord(
    string Name,
    string? Version,
    string? InformationalVersion,
    string? FileVersion,
    string? Location);

public sealed record BasicsTraceabilityRepositoryRecord(
    string Name,
    string RootPath,
    bool IsGitRepository,
    string? HeadCommitSha,
    string? Branch,
    bool? IsDirty,
    string? Error);

public sealed record BasicsBuildTraceRecord(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<BasicsTraceabilityAssemblyRecord> Assemblies,
    IReadOnlyList<BasicsTraceabilityRepositoryRecord> Repositories);

public sealed record BasicsTaskIdentityTraceRecord(
    string TaskId,
    string DisplayName,
    uint InputWidth,
    uint OutputWidth,
    bool UsesTickAlignedEvaluation,
    string Description);

public sealed record BasicsSeedTemplateTraceRecord(
    string TemplateId,
    string Description,
    string? TemplateDefinitionSha256);

public sealed record BasicsInitialBrainSeedTraceRecord(
    string DisplayName,
    bool DuplicateForReproduction,
    string? ContentHash,
    BasicsDefinitionComplexitySummary Complexity,
    bool HasSnapshotBytes);

public sealed record BasicsCapacityTraceRecord(
    string Source,
    int EligibleWorkerCount,
    int RecommendedInitialPopulationCount,
    uint RecommendedReproductionRunCount,
    int RecommendedMaxConcurrentBrains,
    float CapacityScore,
    ulong EffectiveRamFreeBytes,
    string Summary);

public sealed record BasicsOutputSamplingPolicyTraceRecord(
    int MaxReadyWindowTicks,
    int SampleRepeatCount,
    float VectorReadyThreshold);

public sealed record BasicsAdaptiveDiversityTraceRecord(
    bool Enabled,
    int StallGenerationWindow);

public sealed record BasicsSeedVariationBandTraceRecord(
    int MaxInternalNeuronDelta,
    int MaxAxonDelta,
    int MaxStrengthCodeDelta,
    int MaxParameterCodeDelta,
    bool AllowFunctionMutation,
    bool AllowAxonReroute,
    bool AllowRegionSetChange);

public sealed record BasicsSizingOverridesTraceRecord(
    int? InitialPopulationCount,
    int? MinimumPopulationCount,
    int? MaximumPopulationCount,
    uint? ReproductionRunCount,
    int? MaxConcurrentBrains);

public sealed record BasicsParentSelectionTraceRecord(
    double FitnessWeight,
    double DiversityWeight,
    double SpeciesBalanceWeight,
    double EliteFraction,
    double ExplorationFraction,
    int MaxParentsPerSpecies);

public sealed record BasicsRunAllocationTraceRecord(
    uint MinRunsPerPair,
    uint MaxRunsPerPair,
    double FitnessExponent,
    double DiversityBoost);

public sealed record BasicsSchedulingTraceRecord(
    BasicsParentSelectionTraceRecord ParentSelection,
    BasicsRunAllocationTraceRecord RunAllocation);

public sealed record BasicsReproductionTraceRecord(
    string StrengthSource,
    bool ProtectIoRegionNeuronCounts);

public sealed record BasicsMetricsTraceRecord(
    IReadOnlyList<string> RequiredMetrics);

public sealed record BasicsStopCriteriaTraceRecord(
    float TargetAccuracy,
    float TargetFitness,
    bool RequireBothTargets,
    int? MaximumGenerations);

public sealed record BasicsBinaryTruthTableTaskSettingsTraceRecord(
    float LowInputValue,
    float HighInputValue);

public sealed record BasicsScalarGridTaskSettingsTraceRecord(
    int UniqueInputValueCount);

public sealed record BasicsMultiplicationTaskSettingsTraceRecord(
    int UniqueInputValueCount,
    float AccuracyTolerance,
    bool BehaviorOccupancyEnabled,
    float BehaviorStageGateStart,
    float BehaviorStageGateFull);

public sealed record BasicsTaskSettingsTraceRecord(
    BasicsBinaryTruthTableTaskSettingsTraceRecord BooleanTruthTable,
    BasicsScalarGridTaskSettingsTraceRecord Gt,
    BasicsMultiplicationTaskSettingsTraceRecord Multiplication);

public sealed record BasicsExecutionPlanTraceRecord(
    int SchemaVersion,
    DateTimeOffset PlannedAtUtc,
    BasicsTaskIdentityTraceRecord Task,
    BasicsSeedTemplateTraceRecord SeedTemplate,
    IReadOnlyList<BasicsInitialBrainSeedTraceRecord> InitialBrainSeeds,
    BasicsCapacityTraceRecord Capacity,
    string OutputObservationMode,
    BasicsOutputSamplingPolicyTraceRecord OutputSamplingPolicy,
    string DiversityPreset,
    BasicsAdaptiveDiversityTraceRecord AdaptiveDiversity,
    BasicsSeedVariationBandTraceRecord VariationBand,
    BasicsSizingOverridesTraceRecord SizingOverrides,
    BasicsSchedulingTraceRecord Scheduling,
    BasicsReproductionTraceRecord Reproduction,
    BasicsMetricsTraceRecord Metrics,
    BasicsStopCriteriaTraceRecord StopCriteria,
    BasicsTaskSettingsTraceRecord TaskSettings);

public static class BasicsTraceability
{
    public const int SchemaVersion = 3;

    public static BasicsExecutionPlanTraceRecord BuildPlanTrace(BasicsEnvironmentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var taskSettings = plan.TaskSettings ?? new BasicsTaskSettings();
        return new BasicsExecutionPlanTraceRecord(
            SchemaVersion: SchemaVersion,
            PlannedAtUtc: plan.PlannedAtUtc,
            Task: new BasicsTaskIdentityTraceRecord(
                plan.SelectedTask.TaskId,
                plan.SelectedTask.DisplayName,
                plan.SelectedTask.InputWidth,
                plan.SelectedTask.OutputWidth,
                plan.SelectedTask.UsesTickAlignedEvaluation,
                plan.SelectedTask.Description),
            SeedTemplate: new BasicsSeedTemplateTraceRecord(
                plan.SeedTemplate.TemplateId,
                plan.SeedTemplate.Description,
                plan.SeedTemplate.TemplateDefinition?.ToSha256Hex()),
            InitialBrainSeeds: plan.InitialBrainSeeds
                .Select(seed => new BasicsInitialBrainSeedTraceRecord(
                    seed.DisplayName,
                    seed.DuplicateForReproduction,
                    seed.ContentHash,
                    seed.Complexity,
                    seed.SnapshotBytes is { Length: > 0 }))
                .ToArray(),
            Capacity: new BasicsCapacityTraceRecord(
                plan.Capacity.Source.ToString(),
                plan.Capacity.EligibleWorkerCount,
                plan.Capacity.RecommendedInitialPopulationCount,
                plan.Capacity.RecommendedReproductionRunCount,
                plan.Capacity.RecommendedMaxConcurrentBrains,
                plan.Capacity.CapacityScore,
                plan.Capacity.EffectiveRamFreeBytes,
                plan.Capacity.Summary),
            OutputObservationMode: plan.OutputObservationMode.ToString(),
            OutputSamplingPolicy: new BasicsOutputSamplingPolicyTraceRecord(
                plan.OutputSamplingPolicy.MaxReadyWindowTicks,
                plan.OutputSamplingPolicy.SampleRepeatCount,
                plan.OutputSamplingPolicy.VectorReadyThreshold),
            DiversityPreset: plan.DiversityPreset.ToString(),
            AdaptiveDiversity: new BasicsAdaptiveDiversityTraceRecord(
                plan.AdaptiveDiversity.Enabled,
                plan.AdaptiveDiversity.StallGenerationWindow),
            VariationBand: new BasicsSeedVariationBandTraceRecord(
                plan.SeedTemplate.InitialVariationBand.MaxInternalNeuronDelta,
                plan.SeedTemplate.InitialVariationBand.MaxAxonDelta,
                plan.SeedTemplate.InitialVariationBand.MaxStrengthCodeDelta,
                plan.SeedTemplate.InitialVariationBand.MaxParameterCodeDelta,
                plan.SeedTemplate.InitialVariationBand.AllowFunctionMutation,
                plan.SeedTemplate.InitialVariationBand.AllowAxonReroute,
                plan.SeedTemplate.InitialVariationBand.AllowRegionSetChange),
            SizingOverrides: new BasicsSizingOverridesTraceRecord(
                plan.SizingOverrides.InitialPopulationCount,
                plan.SizingOverrides.MinimumPopulationCount,
                plan.SizingOverrides.MaximumPopulationCount,
                plan.SizingOverrides.ReproductionRunCount,
                plan.SizingOverrides.MaxConcurrentBrains),
            Scheduling: new BasicsSchedulingTraceRecord(
                new BasicsParentSelectionTraceRecord(
                    plan.Scheduling.ParentSelection.FitnessWeight,
                    plan.Scheduling.ParentSelection.DiversityWeight,
                    plan.Scheduling.ParentSelection.SpeciesBalanceWeight,
                    plan.Scheduling.ParentSelection.EliteFraction,
                    plan.Scheduling.ParentSelection.ExplorationFraction,
                    plan.Scheduling.ParentSelection.MaxParentsPerSpecies),
                new BasicsRunAllocationTraceRecord(
                    plan.Scheduling.RunAllocation.MinRunsPerPair,
                    plan.Scheduling.RunAllocation.MaxRunsPerPair,
                    plan.Scheduling.RunAllocation.FitnessExponent,
                    plan.Scheduling.RunAllocation.DiversityBoost)),
            Reproduction: new BasicsReproductionTraceRecord(
                plan.Reproduction.StrengthSource.ToString(),
                plan.Reproduction.Config.HasProtectIoRegionNeuronCounts && plan.Reproduction.Config.ProtectIoRegionNeuronCounts),
            Metrics: new BasicsMetricsTraceRecord(
                plan.Metrics.RequiredMetrics.Select(metric => metric.ToString()).ToArray()),
            StopCriteria: new BasicsStopCriteriaTraceRecord(
                plan.StopCriteria.TargetAccuracy,
                plan.StopCriteria.TargetFitness,
                plan.StopCriteria.RequireBothTargets,
                plan.StopCriteria.MaximumGenerations),
            TaskSettings: new BasicsTaskSettingsTraceRecord(
                new BasicsBinaryTruthTableTaskSettingsTraceRecord(
                    taskSettings.BooleanTruthTable.LowInputValue,
                    taskSettings.BooleanTruthTable.HighInputValue),
                new BasicsScalarGridTaskSettingsTraceRecord(
                    taskSettings.Gt.UniqueInputValueCount),
                new BasicsMultiplicationTaskSettingsTraceRecord(
                    taskSettings.Multiplication.UniqueInputValueCount,
                    taskSettings.Multiplication.AccuracyTolerance,
                    taskSettings.Multiplication.BehaviorOccupancyEnabled,
                    taskSettings.Multiplication.BehaviorStageGateStart,
                    taskSettings.Multiplication.BehaviorStageGateFull)));
    }

    public static BasicsBuildTraceRecord BuildBuildTrace(
        IEnumerable<Assembly> assemblies,
        IEnumerable<BasicsTraceabilityRepositoryTarget> repositories)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(repositories);

        return new BasicsBuildTraceRecord(
            SchemaVersion: SchemaVersion,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Assemblies: assemblies
                .Where(assembly => assembly is not null)
                .Distinct()
                .Select(BuildAssemblyRecord)
                .OrderBy(record => record.Name, StringComparer.Ordinal)
                .ToArray(),
            Repositories: repositories
                .Where(target => target is not null && !string.IsNullOrWhiteSpace(target.RootPath))
                .Select(BuildRepositoryRecord)
                .OrderBy(record => record.Name, StringComparer.Ordinal)
                .ToArray());
    }

    private static BasicsTraceabilityAssemblyRecord BuildAssemblyRecord(Assembly assembly)
    {
        var name = assembly.GetName();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? fileVersion = null;
        string? location = null;
        try
        {
            location = string.IsNullOrWhiteSpace(assembly.Location) ? null : assembly.Location;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            {
                fileVersion = FileVersionInfo.GetVersionInfo(location).FileVersion;
            }
        }
        catch
        {
            // Best-effort metadata only.
        }

        return new BasicsTraceabilityAssemblyRecord(
            Name: name.Name ?? assembly.FullName ?? "unknown",
            Version: name.Version?.ToString(),
            InformationalVersion: informationalVersion,
            FileVersion: fileVersion,
            Location: location);
    }

    private static BasicsTraceabilityRepositoryRecord BuildRepositoryRecord(BasicsTraceabilityRepositoryTarget target)
    {
        var rootPath = Path.GetFullPath(target.RootPath);
        try
        {
            var topLevel = RunGit(rootPath, "rev-parse", "--show-toplevel");
            if (topLevel.ExitCode != 0)
            {
                return new BasicsTraceabilityRepositoryRecord(
                    Name: target.Name,
                    RootPath: rootPath,
                    IsGitRepository: false,
                    HeadCommitSha: null,
                    Branch: null,
                    IsDirty: null,
                    Error: string.IsNullOrWhiteSpace(topLevel.Error)
                        ? "git repository unavailable"
                        : topLevel.Error);
            }

            var head = RunGit(rootPath, "rev-parse", "HEAD");
            var branch = RunGit(rootPath, "branch", "--show-current");
            var status = RunGit(rootPath, "status", "--short");

            var errors = new[]
                {
                    head.ExitCode == 0 ? null : head.Error,
                    branch.ExitCode == 0 ? null : branch.Error,
                    status.ExitCode == 0 ? null : status.Error
                }
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .ToArray();

            return new BasicsTraceabilityRepositoryRecord(
                Name: target.Name,
                RootPath: topLevel.Output ?? rootPath,
                IsGitRepository: true,
                HeadCommitSha: head.ExitCode == 0 ? head.Output : null,
                Branch: branch.ExitCode == 0 ? branch.Output : null,
                IsDirty: status.ExitCode == 0 ? !string.IsNullOrWhiteSpace(status.Output) : null,
                Error: errors.Length == 0 ? null : string.Join(" | ", errors));
        }
        catch (Exception ex)
        {
            return new BasicsTraceabilityRepositoryRecord(
                Name: target.Name,
                RootPath: rootPath,
                IsGitRepository: false,
                HeadCommitSha: null,
                Branch: null,
                IsDirty: null,
                Error: ex.GetBaseException().Message);
        }
    }

    private static GitCommandResult RunGit(string workingDirectory, params string[] args)
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
        process.StartInfo.ArgumentList.Add("--no-optional-locks");
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        if (!process.WaitForExit(3000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort kill only.
            }

            return new GitCommandResult(-1, null, "git command timed out");
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        return new GitCommandResult(
            process.ExitCode,
            string.IsNullOrWhiteSpace(output) ? null : output,
            string.IsNullOrWhiteSpace(error) ? null : error);
    }

    private readonly record struct GitCommandResult(int ExitCode, string? Output, string? Error);
}
