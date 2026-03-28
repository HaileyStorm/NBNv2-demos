using Avalonia;
using Avalonia.Threading;

namespace Nbn.Demos.Basics.Ui.Services;

public sealed class UiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        if (Application.Current?.ApplicationLifetime is null)
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }
}
