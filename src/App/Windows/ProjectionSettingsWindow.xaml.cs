using System.Windows;

namespace IPhoneMirror.App.Windows;

public partial class ProjectionSettingsWindow : Window
{
    private readonly Func<Task> _refresh;
    private readonly Func<Task> _fullScreen;
    private readonly Func<Task> _separateWindow;
    private readonly Func<Task> _screenshot;
    private readonly Action _mediaOutput;

    internal ProjectionSettingsWindow(object dataContext,
        Func<Task> refresh, Func<Task> fullScreen,
        Func<Task> separateWindow, Func<Task> screenshot,
        Action mediaOutput)
    {
        InitializeComponent();
        DataContext = dataContext;
        _refresh = refresh;
        _fullScreen = fullScreen;
        _separateWindow = separateWindow;
        _screenshot = screenshot;
        _mediaOutput = mediaOutput;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_refresh);

    private async void OnFullScreenClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_fullScreen);

    private async void OnSeparateWindowClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_separateWindow);

    private async void OnScreenshotClick(object sender, RoutedEventArgs e) =>
        await RunAsync(_screenshot);

    private void OnMediaOutputClick(object sender, RoutedEventArgs e) =>
        _mediaOutput();

    private static async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch
        {
            // The main window actions already publish localized UI and
            // diagnostic errors; the floating panel must remain usable.
        }
    }
}
