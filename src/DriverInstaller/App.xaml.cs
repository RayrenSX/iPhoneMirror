using System.Windows;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (ElevatedDriverHost.IsRequested(e.Args))
        {
            Shutdown(ElevatedDriverHost.Run(e.Args));
            return;
        }

        base.OnStartup(e);
        DriverLocalization.Initialize(e.Args);
        Resources.MergedDictionaries.Insert(0, DriverLocalization.CreateDictionary());
        DriverLogger.Write("Driver manager started.");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
