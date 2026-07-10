using Avalonia.Controls;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;

namespace Nbn.Demos.Basics.Ui;

public partial class MainWindow : Window
{
    private readonly IBasicsLocalWorkerProcessService _workerProcessService;
    private bool _closingAfterShutdown;
    private bool _shutdownInProgress;
    private Task? _shutdownTask;

    public MainWindow()
    {
        InitializeComponent();
        _workerProcessService = new LocalWorkerProcessService();
        DataContext = new MainWindowViewModel(
            new UiDispatcher(),
            new WindowArtifactExportService(this),
            new WindowBrainImportService(this),
            _workerProcessService);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_shutdownInProgress)
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }

        if (!_closingAfterShutdown)
        {
            e.Cancel = true;
            _shutdownInProgress = true;
            _ = CloseAfterShutdownAsync();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Closing is terminal; both cleanup paths were already attempted.
        }
        finally
        {
            base.OnClosed(e);
        }
    }

    private async Task CloseAfterShutdownAsync()
    {
        try
        {
            await ShutdownAsync();
        }
        catch
        {
            // Closing is terminal; both cleanup paths were already attempted.
        }
        finally
        {
            _shutdownInProgress = false;
            _closingAfterShutdown = true;
            Close();
            _closingAfterShutdown = false;
        }
    }

    private Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        Exception? shutdownFailure = null;
        try
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.ShutdownAsync();
            }
        }
        catch (Exception ex)
        {
            shutdownFailure = ex;
        }

        try
        {
            await _workerProcessService.DisposeAsync();
        }
        catch (Exception ex)
        {
            shutdownFailure ??= ex;
        }

        if (shutdownFailure is not null)
        {
            throw shutdownFailure;
        }
    }
}
