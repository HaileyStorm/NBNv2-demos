using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Tasks;

public static class TaskPluginRegistry
{
    private static readonly IReadOnlyDictionary<string, IBasicsTaskPlugin> Implemented =
        new Dictionary<string, IBasicsTaskPlugin>(StringComparer.OrdinalIgnoreCase)
        {
            ["and"] = new AndTaskPlugin()
        };

    public static IReadOnlyList<IBasicsTaskPlugin> ImplementedPlugins { get; } = Implemented.Values.ToArray();

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
