using System.Text.Json.Serialization;
using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;
using Nbn.Shared;
using Repro = Nbn.Proto.Repro;

namespace Nbn.Demos.Basics.Harness;

internal sealed record HarnessFileConfig
{
    public string RunLabel { get; init; } = "basics-live";
    public string OutputDirectory { get; init; } = "artifacts/live-trials";
    public HarnessRuntimeConfig Runtime { get; init; } = new();
    public HarnessTemplatePublishingConfig TemplatePublishing { get; init; } = new();
    public HarnessEnvironmentConfig Environment { get; init; } = new();
    public HarnessTrialConfig Trials { get; init; } = new();

    public static HarnessFileConfig CreateDefault() => new();

    public (BasicsLiveTrialHarnessOptions Options, IBasicsTaskPlugin Plugin) Resolve()
    {
        if (!TaskPluginRegistry.TryGet(Environment.TaskId, out var plugin))
        {
            throw new InvalidOperationException($"Task plugin '{Environment.TaskId}' is not implemented.");
        }

        var selectedTask = plugin.Contract;
        var seedTemplate = new BasicsSeedTemplateContract
        {
            TemplateId = Environment.Template.TemplateId,
            Description = Environment.Template.Description,
            InitialVariationBand = new BasicsSeedVariationBand
            {
                MaxInternalNeuronDelta = Environment.Template.VariationBand.MaxInternalNeuronDelta,
                MaxAxonDelta = Environment.Template.VariationBand.MaxAxonDelta,
                MaxStrengthCodeDelta = Environment.Template.VariationBand.MaxStrengthCodeDelta,
                MaxParameterCodeDelta = Environment.Template.VariationBand.MaxParameterCodeDelta,
                AllowFunctionMutation = Environment.Template.VariationBand.AllowFunctionMutation,
                AllowAxonReroute = Environment.Template.VariationBand.AllowAxonReroute,
                AllowRegionSetChange = Environment.Template.VariationBand.AllowRegionSetChange
            },
            InitialSeedShapeConstraints = new BasicsSeedShapeConstraints
            {
                MinActiveInternalRegionCount = Environment.Template.SeedShape.MinActiveInternalRegionCount,
                MaxActiveInternalRegionCount = Environment.Template.SeedShape.MaxActiveInternalRegionCount,
                MinInternalNeuronCount = Environment.Template.SeedShape.MinInternalNeuronCount,
                MaxInternalNeuronCount = Environment.Template.SeedShape.MaxInternalNeuronCount,
                MinAxonCount = Environment.Template.SeedShape.MinAxonCount,
                MaxAxonCount = Environment.Template.SeedShape.MaxAxonCount
            }
        };

        var scheduling = new BasicsReproductionSchedulingPolicy
        {
            ParentSelection = new BasicsParentSelectionPolicy
            {
                FitnessWeight = Environment.Scheduling.FitnessWeight,
                DiversityWeight = Environment.Scheduling.DiversityWeight,
                SpeciesBalanceWeight = Environment.Scheduling.SpeciesBalanceWeight,
                EliteFraction = Environment.Scheduling.EliteFraction,
                ExplorationFraction = Environment.Scheduling.ExplorationFraction,
                MaxParentsPerSpecies = Environment.Scheduling.MaxParentsPerSpecies
            },
            RunAllocation = new BasicsRunAllocationPolicy
            {
                MinRunsPerPair = Environment.Scheduling.MinRunsPerPair,
                MaxRunsPerPair = Environment.Scheduling.MaxRunsPerPair,
                FitnessExponent = Environment.Scheduling.FitnessExponent,
                DiversityBoost = Environment.Scheduling.DiversityBoost
            }
        };

        var reproduction = BasicsReproductionPolicy.CreateDefault() with
        {
            StrengthSource = ParseStrengthSource(Environment.StrengthSource)
        };

        var options = new BasicsLiveTrialHarnessOptions
        {
            RunLabel = RunLabel,
            RuntimeClient = new BasicsRuntimeClientOptions
            {
                IoAddress = Runtime.IoAddress,
                IoGatewayName = Runtime.IoGatewayName,
                BindHost = Runtime.BindHost,
                Port = Runtime.Port,
                AdvertiseHost = string.IsNullOrWhiteSpace(Runtime.AdvertiseHost) ? null : Runtime.AdvertiseHost.Trim(),
                AdvertisePort = Runtime.AdvertisePort,
                RequestTimeout = TimeSpan.FromSeconds(Runtime.RequestTimeoutSeconds)
            },
            TemplatePublishing = new BasicsTemplatePublishingOptions
            {
                BindHost = TemplatePublishing.BindHost,
                AdvertiseHost = string.IsNullOrWhiteSpace(TemplatePublishing.AdvertiseHost) ? null : TemplatePublishing.AdvertiseHost.Trim(),
                BackingStoreRoot = string.IsNullOrWhiteSpace(TemplatePublishing.BackingStoreRoot) ? null : TemplatePublishing.BackingStoreRoot.Trim()
            },
            Environment = new BasicsEnvironmentOptions
            {
                ClientName = Environment.ClientName,
                SelectedTask = selectedTask,
                SeedTemplate = seedTemplate,
                OutputObservationMode = ParseOutputObservationMode(Environment.OutputObservationMode),
                OutputSamplingPolicy = new BasicsOutputSamplingPolicy
                {
                    MaxReadyWindowTicks = Environment.MaxReadyWindowTicks
                },
                SizingOverrides = new BasicsSizingOverrides
                {
                    InitialPopulationCount = Environment.Sizing.InitialPopulationCount,
                    ReproductionRunCount = Environment.Sizing.ReproductionRunCount,
                    MaxConcurrentBrains = Environment.Sizing.MaxConcurrentBrains
                },
                Reproduction = reproduction,
                Scheduling = scheduling
            },
            MaxTrialCount = Trials.MaxTrialCount,
            TrialTimeout = TimeSpan.FromSeconds(Trials.TrialTimeoutSeconds),
            StabilityCriteria = new BasicsLiveTrialStabilityCriteria
            {
                TargetAccuracy = Trials.TargetAccuracy,
                TargetFitness = Trials.TargetFitness,
                RequiredSuccessfulTrials = Trials.RequiredSuccessfulTrials
            },
            AutoTuning = new BasicsLiveAutoTuningOptions
            {
                Enabled = Trials.AutoTuneEnabled,
                PreferVectorPotentialOnFailures = Trials.PreferVectorPotentialOnFailures,
                ReduceSizingOnFailures = Trials.ReduceSizingOnFailures
            }
        };

        return (options, plugin);
    }

    private static BasicsOutputObservationMode ParseOutputObservationMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "vector-potential" or "continuous-potential" or "potential" => BasicsOutputObservationMode.VectorPotential,
            "evented" or "output-event" or "outputevent" => BasicsOutputObservationMode.EventedOutput,
            "vector-buffer" or "continuous-buffer" or "buffer" => BasicsOutputObservationMode.VectorBuffer,
            _ => throw new InvalidOperationException($"Unsupported output observation mode '{value}'.")
        };
    }

    private static Repro.StrengthSource ParseStrengthSource(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "base-only" or "base" => Repro.StrengthSource.StrengthBaseOnly,
            "live-codes" or "live" => Repro.StrengthSource.StrengthLiveCodes,
            _ => throw new InvalidOperationException($"Unsupported strength source '{value}'.")
        };
    }
}

internal sealed record HarnessRuntimeConfig
{
    public string IoAddress { get; init; } = "127.0.0.1:12050";
    public string IoGatewayName { get; init; } = "io-gateway";
    public string BindHost { get; init; } = NetworkAddressDefaults.DefaultBindHost;
    public int Port { get; init; } = 12096;
    public string AdvertiseHost { get; init; } = string.Empty;
    public int? AdvertisePort { get; init; }
    public int RequestTimeoutSeconds { get; init; } = 30;
}

internal sealed record HarnessTemplatePublishingConfig
{
    public string BindHost { get; init; } = NetworkAddressDefaults.DefaultBindHost;
    public string AdvertiseHost { get; init; } = string.Empty;
    public string BackingStoreRoot { get; init; } = string.Empty;
}

internal sealed record HarnessEnvironmentConfig
{
    public string ClientName { get; init; } = "nbn.basics.harness";
    public string TaskId { get; init; } = "and";
    public string OutputObservationMode { get; init; } = "continuous-potential";
    public int MaxReadyWindowTicks { get; init; } = 4;
    public string StrengthSource { get; init; } = "base-only";
    public HarnessTemplateConfig Template { get; init; } = new();
    public HarnessSizingConfig Sizing { get; init; } = new();
    public HarnessSchedulingConfig Scheduling { get; init; } = new();
}

internal sealed record HarnessTemplateConfig
{
    public string TemplateId { get; init; } = "basics-template-a";
    public string Description { get; init; } =
        "Seed all initial brains from one shared 2->2 template, allowing only bounded minor divergence so reproduction and bootstrap speciation remain coherent.";
    public HarnessVariationBandConfig VariationBand { get; init; } = new();
    public HarnessSeedShapeConfig SeedShape { get; init; } = new();
}

internal sealed record HarnessVariationBandConfig
{
    public int MaxInternalNeuronDelta { get; init; } = 2;
    public int MaxAxonDelta { get; init; } = 8;
    public int MaxStrengthCodeDelta { get; init; } = 4;
    public int MaxParameterCodeDelta { get; init; } = 4;
    public bool AllowFunctionMutation { get; init; }
    public bool AllowAxonReroute { get; init; } = true;
    public bool AllowRegionSetChange { get; init; }
}

internal sealed record HarnessSeedShapeConfig
{
    public int? MinActiveInternalRegionCount { get; init; }
    public int? MaxActiveInternalRegionCount { get; init; }
    public int? MinInternalNeuronCount { get; init; }
    public int? MaxInternalNeuronCount { get; init; }
    public int? MinAxonCount { get; init; }
    public int? MaxAxonCount { get; init; }
}

internal sealed record HarnessSizingConfig
{
    public int? InitialPopulationCount { get; init; } = 256;
    public uint? ReproductionRunCount { get; init; } = 8;
    public int? MaxConcurrentBrains { get; init; } = 128;
}

internal sealed record HarnessSchedulingConfig
{
    public double FitnessWeight { get; init; } = 0.55d;
    public double DiversityWeight { get; init; } = 0.35d;
    public double SpeciesBalanceWeight { get; init; } = 0.15d;
    public double EliteFraction { get; init; } = 0.10d;
    public double ExplorationFraction { get; init; } = 0.25d;
    public int MaxParentsPerSpecies { get; init; } = 8;
    public uint MinRunsPerPair { get; init; } = 2;
    public uint MaxRunsPerPair { get; init; } = 12;
    public double FitnessExponent { get; init; } = 1.20d;
    public double DiversityBoost { get; init; } = 0.35d;
}

internal sealed record HarnessTrialConfig
{
    public int MaxTrialCount { get; init; } = 4;
    public int TrialTimeoutSeconds { get; init; } = 180;
    public float TargetAccuracy { get; init; } = 1f;
    public float TargetFitness { get; init; } = 0.99f;
    public int RequiredSuccessfulTrials { get; init; } = 2;
    public bool AutoTuneEnabled { get; init; } = true;
    public bool PreferVectorPotentialOnFailures { get; init; } = true;
    public bool ReduceSizingOnFailures { get; init; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HarnessFileConfig))]
[JsonSerializable(typeof(BasicsLiveTrialReport))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext
{
}
