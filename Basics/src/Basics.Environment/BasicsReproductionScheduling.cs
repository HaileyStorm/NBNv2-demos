namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsParentSelectionPolicy
{
    public double FitnessWeight { get; init; } = 0.55d;
    public double DiversityWeight { get; init; } = 0.30d;
    public double SpeciesBalanceWeight { get; init; } = 0.15d;
    public double EliteFraction { get; init; } = 0.10d;
    public double ExplorationFraction { get; init; } = 0.20d;
    public int MaxParentsPerSpecies { get; init; } = 8;

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (FitnessWeight < 0d || DiversityWeight < 0d || SpeciesBalanceWeight < 0d)
        {
            errors.Add("Parent selection weights must be >= 0.");
        }

        var totalWeight = FitnessWeight + DiversityWeight + SpeciesBalanceWeight;
        if (totalWeight <= 0d)
        {
            errors.Add("Parent selection weights must sum to > 0.");
        }

        if (EliteFraction is < 0d or > 1d)
        {
            errors.Add("EliteFraction must be between 0 and 1.");
        }

        if (ExplorationFraction is < 0d or > 1d)
        {
            errors.Add("ExplorationFraction must be between 0 and 1.");
        }

        if (EliteFraction + ExplorationFraction > 1d)
        {
            errors.Add("EliteFraction + ExplorationFraction must be <= 1.");
        }

        if (MaxParentsPerSpecies <= 0)
        {
            errors.Add("MaxParentsPerSpecies must be > 0.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public sealed record BasicsRunAllocationPolicy
{
    public const uint MaximumRunsPerPair = 64;

    public uint MinRunsPerPair { get; init; } = 1;
    public uint MaxRunsPerPair { get; init; } = 6;
    public double FitnessExponent { get; init; } = 1.20d;
    public double DiversityBoost { get; init; } = 0.35d;

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (MinRunsPerPair == 0)
        {
            errors.Add("MinRunsPerPair must be > 0.");
        }

        if (MaxRunsPerPair < MinRunsPerPair)
        {
            errors.Add("MaxRunsPerPair must be >= MinRunsPerPair.");
        }

        if (MaxRunsPerPair > MaximumRunsPerPair)
        {
            errors.Add($"MaxRunsPerPair must be <= {MaximumRunsPerPair}.");
        }

        if (FitnessExponent <= 0d)
        {
            errors.Add("FitnessExponent must be > 0.");
        }

        if (DiversityBoost < 0d)
        {
            errors.Add("DiversityBoost must be >= 0.");
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public sealed record BasicsReproductionSchedulingPolicy
{
    public static BasicsReproductionSchedulingPolicy Default { get; } = new();

    public BasicsParentSelectionPolicy ParentSelection { get; init; } = new();
    public BasicsRunAllocationPolicy RunAllocation { get; init; } = new();

    public BasicsContractValidationResult Validate()
    {
        var errors = new List<string>();
        if (!ParentSelection.Validate().IsValid)
        {
            errors.AddRange(ParentSelection.Validate().Errors);
        }

        if (!RunAllocation.Validate().IsValid)
        {
            errors.AddRange(RunAllocation.Validate().Errors);
        }

        return BasicsContractValidationResult.FromErrors(errors);
    }
}

public readonly record struct BasicsParentSelectionScore(
    float WeightedScore,
    float FitnessComponent,
    float DiversityComponent,
    float SpeciesBalanceComponent);

public static class BasicsReproductionBudgetPlanner
{
    public static int ResolveAdaptiveExplorationChildBudget(int offspringSlotCount, int adaptiveBoostSteps)
    {
        if (offspringSlotCount <= 1 || adaptiveBoostSteps <= 0)
        {
            return 0;
        }

        var fraction = adaptiveBoostSteps switch
        {
            1 => 0.20d,
            2 => 0.30d,
            _ => 0.40d
        };
        var budget = (int)Math.Round(offspringSlotCount * fraction, MidpointRounding.AwayFromZero);
        return Math.Clamp(budget, 1, offspringSlotCount - 1);
    }

    public static int ResolveEliteRefinementChildBudget(
        int offspringSlotCount,
        int eliteCount,
        int adaptiveBoostSteps,
        int explorationChildBudget)
    {
        if (offspringSlotCount <= 0 || eliteCount <= 0 || adaptiveBoostSteps <= 0)
        {
            return 0;
        }

        var exploitationSlots = Math.Max(0, offspringSlotCount - Math.Max(0, explorationChildBudget));
        if (exploitationSlots == 0)
        {
            return 0;
        }

        var budget = (int)Math.Ceiling(exploitationSlots * 0.25d);
        return Math.Clamp(budget, 1, exploitationSlots);
    }

    public static BasicsParentSelectionScore ScoreParentCandidate(
        BasicsParentSelectionPolicy policy,
        float normalizedFitness,
        float normalizedNovelty,
        float normalizedSpeciesBalance)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var fitnessComponent = Clamp01(normalizedFitness) * (float)policy.FitnessWeight;
        var diversityComponent = Clamp01(normalizedNovelty) * (float)policy.DiversityWeight;
        var speciesBalanceComponent = Clamp01(normalizedSpeciesBalance) * (float)policy.SpeciesBalanceWeight;
        return new BasicsParentSelectionScore(
            WeightedScore: fitnessComponent + diversityComponent + speciesBalanceComponent,
            FitnessComponent: fitnessComponent,
            DiversityComponent: diversityComponent,
            SpeciesBalanceComponent: speciesBalanceComponent);
    }

    public static uint ResolveRunCount(
        BasicsRunAllocationPolicy policy,
        uint baseRunCount,
        float normalizedFitness,
        float normalizedNovelty)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.MaxRunsPerPair == policy.MinRunsPerPair)
        {
            return policy.MinRunsPerPair;
        }

        var baseline = Math.Min(
            policy.MaxRunsPerPair,
            Math.Max(policy.MinRunsPerPair, baseRunCount == 0 ? policy.MaxRunsPerPair : baseRunCount));
        var weightedFitness = Math.Pow(Clamp01(normalizedFitness), policy.FitnessExponent);
        var diversityBonus = Clamp01(normalizedNovelty) * policy.DiversityBoost;
        var normalizedScore = Math.Min(1d, (weightedFitness + diversityBonus) / (1d + policy.DiversityBoost));
        if (normalizedScore >= 0.5d)
        {
            var highRange = policy.MaxRunsPerPair - baseline;
            var highScore = (normalizedScore - 0.5d) * 2d;
            return baseline + (uint)Math.Round(highRange * highScore, MidpointRounding.AwayFromZero);
        }

        var lowRange = baseline - policy.MinRunsPerPair;
        var lowScore = normalizedScore * 2d;
        return policy.MinRunsPerPair + (uint)Math.Round(lowRange * lowScore, MidpointRounding.AwayFromZero);
    }

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
}
