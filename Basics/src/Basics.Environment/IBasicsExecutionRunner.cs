namespace Nbn.Demos.Basics.Environment;

public interface IBasicsExecutionRunner : IAsyncDisposable
{
    Task<BasicsExecutionSnapshot> RunAsync(
        BasicsEnvironmentPlan plan,
        IBasicsTaskPlugin taskPlugin,
        Action<BasicsExecutionSnapshot>? onSnapshot,
        CancellationToken cancellationToken = default);
}
