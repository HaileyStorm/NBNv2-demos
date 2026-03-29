using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

public static class TaskPluginRegistry
{
    private static readonly IReadOnlyList<IBasicsTaskPlugin> Plugins =
    [
        new AndTaskPlugin(),
        new OrTaskPlugin(),
        new XorTaskPlugin(),
        new GtTaskPlugin(),
        new MultiplicationTaskPlugin()
    ];

    private static readonly IReadOnlyDictionary<string, IBasicsTaskPlugin> Implemented =
        Plugins.ToDictionary(plugin => plugin.Contract.TaskId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<IBasicsTaskPlugin> ImplementedPlugins { get; } = Plugins;

    public static bool TryGet(string taskId, out IBasicsTaskPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            plugin = null!;
            return false;
        }

        return Implemented.TryGetValue(taskId.Trim(), out plugin!);
    }
}
