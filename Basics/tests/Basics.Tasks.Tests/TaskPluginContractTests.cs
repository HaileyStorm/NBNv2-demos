using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class TaskPluginContractTests
{
    public static TheoryData<IBasicsTaskPlugin> ImplementedPlugins { get; } = CreateImplementedPlugins();

    [Theory]
    [MemberData(nameof(ImplementedPlugins))]
    public void ImplementedPlugins_AdvertiseSharedTwoByOneTickAlignedContract(IBasicsTaskPlugin plugin)
    {
        Assert.Equal(BasicsIoGeometry.InputWidth, plugin.Contract.InputWidth);
        Assert.Equal(BasicsIoGeometry.OutputWidth, plugin.Contract.OutputWidth);
        Assert.True(plugin.Contract.UsesTickAlignedEvaluation);
        Assert.NotEmpty(plugin.Contract.Description);
        Assert.NotEmpty(plugin.BuildDeterministicDataset());
    }

    [Theory]
    [MemberData(nameof(ImplementedPlugins))]
    public void ImplementedPlugins_Fail_WhenTickAlignmentIsMissing(IBasicsTaskPlugin plugin)
    {
        var dataset = plugin.BuildDeterministicDataset();
        var observations = CreatePerfectObservations(dataset);

        var result = plugin.Evaluate(
            new BasicsTaskEvaluationContext(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: false),
            dataset,
            observations);

        Assert.Equal(0f, result.Fitness);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("tick_alignment_required", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ImplementedPlugins))]
    public void ImplementedPlugins_Fail_WhenDatasetDoesNotMatchCanonicalDefinition(IBasicsTaskPlugin plugin)
    {
        var canonical = plugin.BuildDeterministicDataset();
        var mutated = canonical.ToArray();
        mutated[0] = mutated[0] with
        {
            ExpectedOutput = mutated[0].ExpectedOutput == 0f ? 1f : 0f
        };

        var result = plugin.Evaluate(
            CreateValidContext(),
            mutated,
            CreatePerfectObservations(mutated));

        Assert.Equal(0f, result.Fitness);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("dataset_sample_mismatch", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ImplementedPlugins))]
    public void ImplementedPlugins_Fail_WhenDatasetCardinalityShrinks(IBasicsTaskPlugin plugin)
    {
        var dataset = plugin.BuildDeterministicDataset().Take(plugin.BuildDeterministicDataset().Count - 1).ToArray();

        var result = plugin.Evaluate(
            CreateValidContext(),
            dataset,
            CreatePerfectObservations(dataset));

        Assert.Equal(0f, result.Fitness);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("dataset_cardinality_mismatch", StringComparison.Ordinal));
    }

    private static BasicsTaskEvaluationContext CreateValidContext()
        => new(BasicsIoGeometry.InputWidth, BasicsIoGeometry.OutputWidth, TickAligned: true);

    private static TheoryData<IBasicsTaskPlugin> CreateImplementedPlugins()
    {
        var data = new TheoryData<IBasicsTaskPlugin>();
        data.Add(new AndTaskPlugin());
        data.Add(new OrTaskPlugin());
        data.Add(new XorTaskPlugin());
        data.Add(new GtTaskPlugin());
        data.Add(new MultiplicationTaskPlugin());
        return data;
    }

    private static BasicsTaskObservation[] CreatePerfectObservations(IReadOnlyList<BasicsTaskSample> dataset)
        => dataset
            .Select((sample, index) => new BasicsTaskObservation((ulong)(index + 1), sample.ExpectedOutput))
            .ToArray();
}
