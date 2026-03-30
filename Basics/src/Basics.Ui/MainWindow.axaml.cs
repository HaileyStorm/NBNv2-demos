using Avalonia.Controls;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;

namespace Nbn.Demos.Basics.Ui;

public partial class MainWindow : Window
{
    private readonly IBasicsLocalWorkerProcessService _workerProcessService;

    public MainWindow()
    {
        InitializeComponent();
        _workerProcessService = new LocalWorkerProcessService();
        DataContext = new MainWindowViewModel(
            new UiDispatcher(),
            new WindowArtifactExportService(this),
            _workerProcessService);
        Closed += async (_, _) => await _workerProcessService.DisposeAsync();
    }
}
