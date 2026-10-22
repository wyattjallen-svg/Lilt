using Lilt.Core;
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
            LiltPaths.LogError(e.Exception);
            if (Interlocked.Increment(ref faults) > 20 || window is null) return;
            e.Handled = true;
            window.Fault(e.Exception.GetType().Name);
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
