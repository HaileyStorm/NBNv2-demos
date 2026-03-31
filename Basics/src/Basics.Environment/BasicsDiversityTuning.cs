using Repro = Nbn.Proto.Repro;

namespace Nbn.Demos.Basics.Environment;

public static class BasicsDiversityTuning
{
    public static BasicsSeedVariationBand CreateVariationBand(BasicsDiversityPreset preset)
        => preset switch
        {
            BasicsDiversityPreset.Low => new BasicsSeedVariationBand
            {
                MaxInternalNeuronDelta = 1,
                MaxAxonDelta = 2,
                MaxStrengthCodeDelta = 2,
                MaxParameterCodeDelta = 1,
                AllowFunctionMutation = false,
                AllowAxonReroute = false,
                AllowRegionSetChange = false
            },
            BasicsDiversityPreset.High => new BasicsSeedVariationBand
            {
                MaxInternalNeuronDelta = 3,
                MaxAxonDelta = 10,
                MaxStrengthCodeDelta = 6,
                MaxParameterCodeDelta = 6,
                AllowFunctionMutation = true,
                AllowAxonReroute = true,
                AllowRegionSetChange = false
            },
            BasicsDiversityPreset.Extreme => new BasicsSeedVariationBand
            {
                MaxInternalNeuronDelta = 4,
                MaxAxonDelta = 14,
                MaxStrengthCodeDelta = 8,
                MaxParameterCodeDelta = 8,
                AllowFunctionMutation = true,
                AllowAxonReroute = true,
                AllowRegionSetChange = false
            },
            _ => new BasicsSeedVariationBand
            {
                MaxInternalNeuronDelta = 2,
                MaxAxonDelta = 8,
                MaxStrengthCodeDelta = 4,
                MaxParameterCodeDelta = 4,
                AllowFunctionMutation = false,
                AllowAxonReroute = true,
                AllowRegionSetChange = false
            }
        };

    public static BasicsReproductionSchedulingPolicy CreateScheduling(BasicsDiversityPreset preset)
        => preset switch
        {
            BasicsDiversityPreset.Low => new BasicsReproductionSchedulingPolicy
            {
                ParentSelection = new BasicsParentSelectionPolicy
                {
                    FitnessWeight = 0.65d,
                    DiversityWeight = 0.20d,
                    SpeciesBalanceWeight = 0.15d,
                    EliteFraction = 0.14d,
                    ExplorationFraction = 0.10d,
                    MaxParentsPerSpecies = 6
                },
                RunAllocation = new BasicsRunAllocationPolicy
                {
                    MinRunsPerPair = 1,
                    MaxRunsPerPair = 6,
                    FitnessExponent = 1.30d,
                    DiversityBoost = 0.15d
                }
            },
            BasicsDiversityPreset.High => new BasicsReproductionSchedulingPolicy
            {
                ParentSelection = new BasicsParentSelectionPolicy
                {
                    FitnessWeight = 0.50d,
                    DiversityWeight = 0.43d,
                    SpeciesBalanceWeight = 0.15d,
                    EliteFraction = 0.25d,
                    ExplorationFraction = 0.53d,
                    MaxParentsPerSpecies = 8
                },
                RunAllocation = new BasicsRunAllocationPolicy
                {
                    MinRunsPerPair = 3,
                    MaxRunsPerPair = 8,
                    FitnessExponent = 1.10d,
                    DiversityBoost = 0.60d
                }
            },
            BasicsDiversityPreset.Extreme => new BasicsReproductionSchedulingPolicy
            {
                ParentSelection = new BasicsParentSelectionPolicy
                {
                    FitnessWeight = 0.40d,
                    DiversityWeight = 0.60d,
                    SpeciesBalanceWeight = 0.20d,
                    EliteFraction = 0.05d,
                    ExplorationFraction = 0.55d,
                    MaxParentsPerSpecies = 14
                },
                RunAllocation = new BasicsRunAllocationPolicy
                {
                    MinRunsPerPair = 3,
                    MaxRunsPerPair = 10,
                    FitnessExponent = 1.00d,
                    DiversityBoost = 0.85d
                }
            },
            _ => new BasicsReproductionSchedulingPolicy
            {
                ParentSelection = new BasicsParentSelectionPolicy
                {
                    FitnessWeight = 0.55d,
                    DiversityWeight = 0.35d,
                    SpeciesBalanceWeight = 0.15d,
                    EliteFraction = 0.10d,
                    ExplorationFraction = 0.25d,
                    MaxParentsPerSpecies = 8
                },
                RunAllocation = new BasicsRunAllocationPolicy
                {
                    MinRunsPerPair = 2,
                    MaxRunsPerPair = 12,
                    FitnessExponent = 1.20d,
                    DiversityBoost = 0.35d
                }
            }
        };

    public static void ApplyPresetToConfig(Repro.ReproduceConfig config, BasicsDiversityPreset preset)
    {
        ArgumentNullException.ThrowIfNull(config);

        switch (preset)
        {
            case BasicsDiversityPreset.Low:
                config.ProbAddAxon = RaiseProbability(config.ProbAddAxon, 0.03f);
                config.ProbRemoveAxon = RaiseProbability(config.ProbRemoveAxon, 0.01f);
                config.ProbRerouteAxon = RaiseProbability(config.ProbRerouteAxon, 0.01f);
                config.ProbDisableNeuron = RaiseProbability(config.ProbDisableNeuron, 0.01f);
                config.ProbReactivateNeuron = RaiseProbability(config.ProbReactivateNeuron, 0.01f);
                config.ProbMutate = RaiseProbability(config.ProbMutate, 0.04f);
                config.ProbMutateFunc = 0f;
                config.ProbStrengthMutate = RaiseProbability(config.ProbStrengthMutate, 0.03f);
                break;
            case BasicsDiversityPreset.High:
                config.ProbAddAxon = RaiseProbability(config.ProbAddAxon, 0.10f);
                config.ProbRemoveAxon = RaiseProbability(config.ProbRemoveAxon, 0.04f);
                config.ProbRerouteAxon = RaiseProbability(config.ProbRerouteAxon, 0.06f);
                config.ProbDisableNeuron = RaiseProbability(config.ProbDisableNeuron, 0.03f);
                config.ProbReactivateNeuron = RaiseProbability(config.ProbReactivateNeuron, 0.03f);
                config.ProbMutate = RaiseProbability(config.ProbMutate, 0.10f);
                config.ProbMutateFunc = RaiseProbability(config.ProbMutateFunc, 0.05f);
                config.ProbStrengthMutate = RaiseProbability(config.ProbStrengthMutate, 0.10f);
                break;
            case BasicsDiversityPreset.Extreme:
                config.ProbAddAxon = RaiseProbability(config.ProbAddAxon, 0.18f);
                config.ProbRemoveAxon = RaiseProbability(config.ProbRemoveAxon, 0.08f);
                config.ProbRerouteAxon = RaiseProbability(config.ProbRerouteAxon, 0.10f);
                config.ProbDisableNeuron = RaiseProbability(config.ProbDisableNeuron, 0.05f);
                config.ProbReactivateNeuron = RaiseProbability(config.ProbReactivateNeuron, 0.05f);
                config.ProbMutate = RaiseProbability(config.ProbMutate, 0.18f);
                config.ProbMutateFunc = RaiseProbability(config.ProbMutateFunc, 0.10f);
                config.ProbStrengthMutate = RaiseProbability(config.ProbStrengthMutate, 0.16f);
                break;
            default:
                config.ProbAddAxon = RaiseProbability(config.ProbAddAxon, 0.05f);
                config.ProbRemoveAxon = RaiseProbability(config.ProbRemoveAxon, 0.02f);
                config.ProbRerouteAxon = RaiseProbability(config.ProbRerouteAxon, 0.02f);
                config.ProbDisableNeuron = RaiseProbability(config.ProbDisableNeuron, 0.01f);
                config.ProbReactivateNeuron = RaiseProbability(config.ProbReactivateNeuron, 0.01f);
                config.ProbMutate = RaiseProbability(config.ProbMutate, 0.05f);
                config.ProbMutateFunc = RaiseProbability(config.ProbMutateFunc, 0.02f);
                config.ProbStrengthMutate = RaiseProbability(config.ProbStrengthMutate, 0.05f);
                break;
        }
    }

    public static int ResolveAdaptiveBoostSteps(BasicsAdaptiveDiversityOptions options, int stalledGenerationCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || stalledGenerationCount < options.StallGenerationWindow)
        {
            return 0;
        }

        return Math.Min(3, 1 + ((stalledGenerationCount - options.StallGenerationWindow) / 2));
    }

    public static BasicsDiversityPreset ResolveEffectivePreset(BasicsDiversityPreset basePreset, int boostSteps)
        => (BasicsDiversityPreset)Math.Min((int)BasicsDiversityPreset.Extreme, (int)basePreset + Math.Max(0, boostSteps));

    public static BasicsReproductionSchedulingPolicy ApplyAdaptiveBoost(
        BasicsReproductionSchedulingPolicy policy,
        int boostSteps)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (boostSteps <= 0)
        {
            return policy;
        }

        var exploration = Math.Clamp(policy.ParentSelection.ExplorationFraction + (0.07d * boostSteps), 0d, 0.70d);
        var elite = Math.Clamp(policy.ParentSelection.EliteFraction - (0.02d * boostSteps), 0.02d, 1d);
        if (elite + exploration > 1d)
        {
            elite = Math.Max(0.02d, 1d - exploration);
        }

        return policy with
        {
            ParentSelection = policy.ParentSelection with
            {
                DiversityWeight = Math.Clamp(policy.ParentSelection.DiversityWeight + (0.05d * boostSteps), 0d, 0.75d),
                SpeciesBalanceWeight = Math.Clamp(policy.ParentSelection.SpeciesBalanceWeight + (0.02d * boostSteps), 0d, 0.35d),
                ExplorationFraction = exploration,
                EliteFraction = elite,
                MaxParentsPerSpecies = Math.Min(16, policy.ParentSelection.MaxParentsPerSpecies + (2 * boostSteps))
            },
            RunAllocation = policy.RunAllocation with
            {
                MaxRunsPerPair = Math.Min(24u, policy.RunAllocation.MaxRunsPerPair + (uint)(2 * boostSteps)),
                FitnessExponent = Math.Clamp(policy.RunAllocation.FitnessExponent - (0.05d * boostSteps), 0.85d, 2.0d),
                DiversityBoost = Math.Clamp(policy.RunAllocation.DiversityBoost + (0.10d * boostSteps), 0d, 1.50d)
            }
        };
    }

    public static void ApplyAdaptiveBoost(Repro.ReproduceConfig config, int boostSteps)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (boostSteps <= 0)
        {
            return;
        }

        var scale = 1f + (0.50f * boostSteps);
        config.ProbAddAxon = ScaleProbability(config.ProbAddAxon, scale, 0.45f);
        config.ProbRemoveAxon = ScaleProbability(config.ProbRemoveAxon, scale, 0.25f);
        config.ProbRerouteAxon = ScaleProbability(config.ProbRerouteAxon, scale, 0.25f);
        config.ProbDisableNeuron = ScaleProbability(config.ProbDisableNeuron, scale, 0.18f);
        config.ProbReactivateNeuron = ScaleProbability(config.ProbReactivateNeuron, scale, 0.18f);
        config.ProbMutate = ScaleProbability(config.ProbMutate, scale, 0.35f);
        config.ProbMutateFunc = ScaleProbability(config.ProbMutateFunc, scale, 0.25f);
        config.ProbStrengthMutate = ScaleProbability(config.ProbStrengthMutate, scale, 0.30f);
    }

    private static float RaiseProbability(float value, float minimum)
        => Math.Max(value, minimum);

    private static float ScaleProbability(float value, float scale, float ceiling)
        => Math.Min(ceiling, Math.Max(value, value * scale));
}
