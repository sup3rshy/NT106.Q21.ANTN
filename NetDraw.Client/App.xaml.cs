using System.Windows;
using NetDraw.Client.Drawing;
using NetDraw.Client.Infrastructure;
using NetDraw.Client.Services;
using NetDraw.Client.ViewModels;

namespace NetDraw.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var events = EventAggregator.Instance;
        var networkService = new NetworkService();
        var fileService = new FileService();
        var historyManager = new HistoryManager();
        var renderer = new WpfCanvasRenderer();

        var mainVm = new MainViewModel(networkService, fileService, historyManager, events);

        var mainWindow = new MainWindow(mainVm, renderer, historyManager, events);
        mainWindow.Show();
    }
}
