using Nbn.Demos.Basics.Harness;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class HarnessConfigurationTests
{
    [Fact]
    public void Resolve_MapsTrialTargetsAndGenerationLimitToExecutionStopCriteria()
    {
        var config = HarnessFileConfig.CreateDefault() with
        {
            Environment = HarnessFileConfig.CreateDefault().Environment with
            {
                TaskId = "multiplication"
            },
            Trials = HarnessFileConfig.CreateDefault().Trials with
            {
                TargetAccuracy = 0.75f,
                TargetFitness = 0.8f,
                MaximumGenerations = 12
            }
        };

        var (options, plugin) = config.Resolve();

        Assert.Equal("multiplication", plugin.Contract.TaskId);
        Assert.Equal(0.75f, options.Environment.StopCriteria.TargetAccuracy);
        Assert.Equal(0.8f, options.Environment.StopCriteria.TargetFitness);
        Assert.Equal(12, options.Environment.StopCriteria.MaximumGenerations);
    }
}
