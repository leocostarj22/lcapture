using System.Windows;
using LCapture.Services;

namespace LCapture;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version;
        Log.Info($"--- LCapture {version} a iniciar (Windows {Environment.OSVersion.Version}) ---");

        DispatcherUnhandledException += (_, args) => Log.Error("Erro não tratado na interface", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Erro fatal", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Erro numa tarefa em segundo plano", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }
}
