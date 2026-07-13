using System.Windows;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Initialize();
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
