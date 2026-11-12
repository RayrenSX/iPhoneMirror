using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void OpenDeveloperThroughAbout(MainWindow owner)
    {
        var app = (App)Application.Current;
        var vm = (ViewModels.MainViewModel)KeyboardField(owner, "_viewModel");
        var about = new AboutWindow(app, vm) { Owner = owner };
        try
        {
            about.Show(); DrainDispatcher();
            var button = (Button)about.FindName("AboutVersionButton");
            var previous = KeyboardField(owner, "_developerToolsWindow");
            for (var index = 0; index < 4; index++) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(ReferenceEquals(previous, KeyboardField(owner, "_developerToolsWindow")),
                "Four About version clicks opened developer tools.");
            SetKeyboardField(about, "_lastVersionClickUtc", DateTime.UtcNow.AddSeconds(-3));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert((int)KeyboardField(about, "_versionClickCount") == 1,
                "An expired click sequence did not reset.");
            for (var index = 0; index < 4; index++) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(KeyboardField(owner, "_developerToolsWindow") is DeveloperToolsWindow { IsVisible: true },
                "Five About version clicks did not open developer tools.");
            InteractionAssert(owner.FindName("VersionButton") is null && owner.FindName("VersionLabel") is TextBlock,
                "The old footer version entry is still interactive.");
        }
        finally { about.Close(); }
    }
}
