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

        // Global exception handlers - log to file
        string logFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
        DispatcherUnhandledException += (s, ex) =>
        {
            System.IO.File.AppendAllText(logFile, $"[UI] {DateTime.Now}: {ex.Exception}\n\n");
            MessageBox.Show($"UI Error: {ex.Exception.Message}\n\n{ex.Exception.StackTrace}", "Error");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            var err = ex.ExceptionObject as Exception;
            System.IO.File.AppendAllText(logFile, $"[Fatal] {DateTime.Now}: {err}\n\n");
        };
        TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            System.IO.File.AppendAllText(logFile, $"[Task] {DateTime.Now}: {ex.Exception}\n\n");
            ex.SetObserved();
        };

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
