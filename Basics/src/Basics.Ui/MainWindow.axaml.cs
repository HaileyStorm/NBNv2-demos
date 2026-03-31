using Avalonia.Controls;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;

namespace Nbn.Demos.Basics.Ui;

public partial class MainWindow : Window
{
    private readonly IBasicsLocalWorkerProcessService _workerProcessService;
    private bool _closingAfterWorkerShutdown;
    private bool _workerShutdownInProgress;

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
        if (_workerShutdownInProgress)
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }

        if (!_closingAfterWorkerShutdown && _workerProcessService.LaunchedWorkerCount > 0)
        {
            e.Cancel = true;
            _workerShutdownInProgress = true;
            _ = CloseAfterWorkerShutdownAsync();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _workerProcessService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnClosed(e);
    }

    private async Task CloseAfterWorkerShutdownAsync()
    {
        try
        {
            await _workerProcessService.DisposeAsync();
        }
        finally
        {
            _workerShutdownInProgress = false;
            _closingAfterWorkerShutdown = true;
            Close();
            _closingAfterWorkerShutdown = false;
        }
    }
}
