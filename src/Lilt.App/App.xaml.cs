using Lilt.Core;
using Lilt.Core.Diagnostics;
using Microsoft.UI.Xaml;

namespace Lilt.App;

public partial class App : Application
{
    MainWindow? window;
    int faults;

    public App()
    {
        InitializeComponent();
        // An error on the UI thread is logged and shown, and Lilt carries on, unless errors keep coming (then it stops).
        UnhandledException += (_, e) =>
        {
            LiltPaths.LogError(e.Exception, "ui");
            if (Interlocked.Increment(ref faults) > 20 || window is null)
            {
                LiltPaths.Log.Write(LogLevel.Crash, "lilt", "Lilt stopped after an error it couldn't recover from", e.Exception.ToString());
                return;
            }
            e.Handled = true;
            window.Fault(e.Exception.GetType().Name);
        };
        // An error off the UI thread ends the process; this is the last chance to write down why.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LiltPaths.Log.Write(LogLevel.Crash, "lilt",
            "Lilt stopped: " + (e.ExceptionObject is Exception x ? $"{x.GetType().Name}: {x.Message}" : "an unknown error"), e.ExceptionObject.ToString() ?? "");
        // Work started in the background that failed with nobody waiting for it (usually a page closing mid-way).
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var x = e.Exception.GetBaseException();
            LiltPaths.Log.Write(LogLevel.Warning, "task", $"{x.GetType().Name}: {x.Message}", x.ToString());
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dev = Environment.GetEnvironmentVariable("LILT_DEV") == "1";
        var options = LaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray(), dev);
        window = new MainWindow(options);
        window.Activate();
    }
}
