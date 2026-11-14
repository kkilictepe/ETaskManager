using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;

namespace ETaskManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Stay responsive when other processes saturate every core: run above them.
        // (High, like Task Manager / Process Explorer. Never Realtime.)
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never let one bad tick take the monitor down.
        Debug.WriteLine(e.Exception);
        e.Handled = true;
        if (MainWindow is MainWindow w) w.ShowStatus("Error: " + e.Exception.Message);
    }
}
