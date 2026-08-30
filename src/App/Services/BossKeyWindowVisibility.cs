using System.Windows;

namespace IPhoneMirror.App.Services;

internal static class BossKeyWindowVisibility
{
    private static readonly HashSet<Window> HiddenWindows = [];
    private static bool _hidden;

    internal static int HiddenWindowCount => HiddenWindows.Count;

    internal static void Apply(Window window)
    {
        window.Closed += OnWindowClosed;
        if (!_hidden || !window.IsVisible) return;
        HiddenWindows.Add(window);
        window.Hide();
    }

    internal static void HideAll()
    {
        _hidden = true;
        foreach (var window in Application.Current?.Windows.Cast<Window>()
            .Where(window => window.IsVisible).ToArray() ?? [])
        {
            HiddenWindows.Add(window);
            window.Hide();
        }
    }

    internal static void RestoreAll()
    {
        _hidden = false;
        foreach (var window in HiddenWindows
            .OrderBy(window => ReferenceEquals(window, Application.Current?.MainWindow) ? 0 : 1)
            .ToArray())
        {
            if (window.IsLoaded && !window.IsVisible) window.Show();
        }
        HiddenWindows.Clear();
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Closed -= OnWindowClosed;
        HiddenWindows.Remove(window);
    }
}
