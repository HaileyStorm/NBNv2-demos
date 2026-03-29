using Nbn.Demos.Basics.Tasks;

namespace Nbn.Demos.Basics.Tasks.Tests;

public sealed class TaskPluginRegistryTests
{
    [Theory]
    [InlineData("and", "AND")]
    [InlineData("OR", "OR")]
    [InlineData("xor", "XOR")]
    [InlineData("Gt", "GT")]
    [InlineData("MULTIPLICATION", "Multiplication")]
    public void TryGet_ResolvesImplementedTasks_CaseInsensitively(string taskId, string displayName)
    {
        Assert.True(TaskPluginRegistry.TryGet(taskId, out var plugin));
        Assert.Equal(displayName, plugin.Contract.DisplayName);
    }

    [Fact]
    public void ImplementedPlugins_ContainsEachImplementedTaskExactlyOnce()
    {
        var taskIds = TaskPluginRegistry.ImplementedPlugins
            .Select(plugin => plugin.Contract.TaskId)
            .ToArray();

        Assert.Equal(5, taskIds.Length);
        Assert.Equal(taskIds.Length, taskIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("and", taskIds);
        Assert.Contains("or", taskIds);
        Assert.Contains("xor", taskIds);
        Assert.Contains("gt", taskIds);
        Assert.Contains("multiplication", taskIds);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnknownTask()
    {
        Assert.False(TaskPluginRegistry.TryGet("denoise", out _));
    }
}
