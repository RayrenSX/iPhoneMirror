using System.Windows;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

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
        if (SecondaryMirrorMode.TryParse(e.Args, out var request))
        {
            MainWindow = new SecondaryMirrorWindow(request);
            MainWindow.Show();
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
