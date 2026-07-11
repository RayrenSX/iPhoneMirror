using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (DriverHelperMode.IsRequested(e.Args))
        {
            var exitCode = DriverHelperMode.Run(e.Args);
            Shutdown(exitCode);
            return;
        }
        LocalizationService.Initialize();
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
