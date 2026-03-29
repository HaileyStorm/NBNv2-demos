using Avalonia.Controls;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;

namespace Nbn.Demos.Basics.Ui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(
            new UiDispatcher(),
            new WindowArtifactExportService(this));
    }
}
