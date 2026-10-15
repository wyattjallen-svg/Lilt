using Lilt.Core;
using Microsoft.UI.Xaml;

namespace Lilt.App;

public partial class App : Application
{
    MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LiltPaths.LogError(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = LaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        window = new MainWindow(options);
        window.Activate();
    }
}
