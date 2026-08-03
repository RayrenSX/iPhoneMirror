using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using IPhoneMirror.App.Updater;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace IPhoneMirror.App.Services;

internal static class ThemeService
{
    private const string LightThemePath = "Themes/LightTheme.xaml";
    private const string DarkThemePath = "Themes/DarkTheme.xaml";
    private sealed class WindowBackdropState
    {
        internal bool EdgeToEdge { get; set; }
    }

    private static readonly ConditionalWeakTable<Window, WindowBackdropState> AttachedWindows = new();
    private static bool _systemEventsAttached;

    internal static bool IsDark { get; private set; } = true;
    internal static AppTheme Preference { get; private set; } = AppTheme.System;

    internal static void Apply(AppTheme theme)
    {
        Preference = theme;
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };
        EnsureSystemEvents();

        var application = Application.Current;
        if (application is null) return;
        SwapThemeDictionary(application.Resources, IsDark ? DarkThemePath : LightThemePath);
        var applicationTheme = CurrentApplicationTheme;
        ApplicationThemeManager.Apply(
            applicationTheme, Wpf.Ui.Controls.WindowBackdropType.None,
            updateAccent: false);
        foreach (Window window in application.Windows)
        {
            RefreshWindowTheme(window, applicationTheme);
            AnimateThemeTransition(window);
        }
    }

    internal static void Attach(Window window)
    {
        if (AttachedWindows.TryGetValue(window, out _)) return;
        AttachedWindows.Add(window, new WindowBackdropState());
        if (!window.AllowsTransparency)
            window.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
        window.SourceInitialized += (_, _) =>
            RefreshWindowTheme(window, CurrentApplicationTheme);
        window.StateChanged += (_, _) => ApplyBackdrop(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            RefreshWindowTheme(window, CurrentApplicationTheme);
    }

    internal static void SetEdgeToEdge(Window window, bool enabled)
    {
        Attach(window);
        if (!AttachedWindows.TryGetValue(window, out var state)) return;
        state.EdgeToEdge = enabled;
        RefreshWindowTheme(window, CurrentApplicationTheme);
    }

    internal static void Shutdown()
    {
        if (!_systemEventsAttached) return;
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _systemEventsAttached = false;
    }

    private static void SwapThemeDictionary(ResourceDictionary resources, string path)
    {
        var existing = resources.MergedDictionaries.FirstOrDefault(dictionary =>
            IsThemeDictionary(dictionary.Source));
        if (existing?.Source?.OriginalString.EndsWith(path,
                StringComparison.OrdinalIgnoreCase) == true)
            return;

        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                $"/{typeof(ThemeService).Assembly.GetName().Name};component/{path}",
                UriKind.Relative),
        };
        if (existing is null)
            resources.MergedDictionaries.Insert(0, replacement);
        else
        {
            var index = resources.MergedDictionaries.IndexOf(existing);
            resources.MergedDictionaries[index] = replacement;
        }
    }

    private static bool IsThemeDictionary(Uri? source)
    {
        var value = source?.OriginalString;
        return value?.EndsWith(LightThemePath, StringComparison.OrdinalIgnoreCase) == true ||
               value?.EndsWith(DarkThemePath, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void AnimateThemeTransition(Window window)
    {
        if (!window.IsLoaded) return;
        window.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static ApplicationTheme CurrentApplicationTheme =>
        IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;

    private static void RefreshWindowTheme(Window window,
        ApplicationTheme applicationTheme)
    {
        if (!window.AllowsTransparency)
        {
            var backdrop = window is Wpf.Ui.Controls.FluentWindow fluentWindow
                ? fluentWindow.WindowBackdropType
                : Wpf.Ui.Controls.WindowBackdropType.None;
            WindowBackgroundManager.UpdateBackground(window, applicationTheme, backdrop);
        }
        ApplyBackdrop(window);
    }

    private static void EnsureSystemEvents()
    {
        if (_systemEventsAttached) return;
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        _systemEventsAttached = true;
    }

    private static void OnSystemPreferenceChanged(object sender,
        UserPreferenceChangedEventArgs args)
    {
        if (Preference != AppTheme.System || Application.Current is null) return;
        _ = Application.Current.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
    }

    private static void ApplyBackdrop(Window window)
    {
        if (window.AllowsTransparency) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        _ = AttachedWindows.TryGetValue(window, out var state);
        var edgeToEdge = state?.EdgeToEdge == true;
        var flushToDisplayEdge = edgeToEdge || window.WindowState == WindowState.Maximized;
        var dark = IsDark ? 1 : 0;
        var corner = flushToDisplayEdge ? DwmDoNotRound : DwmRound;
        var acrylic = window is Wpf.Ui.Controls.FluentWindow fluentWindow &&
                      fluentWindow.WindowBackdropType ==
                          Wpf.Ui.Controls.WindowBackdropType.Acrylic;
        var backdrop = edgeToEdge
            ? DwmBackdropNone
            : acrylic ? DwmBackdropAcrylic : DwmBackdropMica;
        var border = flushToDisplayEdge ? DwmColorNone : DwmColorDefault;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, sizeof(int));
        _ = DwmSetWindowAttributeColor(handle, DwmBorderColor, ref border, sizeof(uint));
    }

    private static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            return value is not int integer || integer == 0;
        }
        catch (Exception error)
        {
            DiagnosticLogger.ExceptionOnce("theme-registry", "theme",
                "system_theme_read_failed", error);
            return true;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
        ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeColor(IntPtr window, int attribute,
        ref uint value, int valueSize);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmSystemBackdropType = 38;
    private const int DwmDoNotRound = 1;
    private const int DwmRound = 2;
    private const int DwmBackdropNone = 1;
    private const int DwmBackdropMica = 2;
    private const int DwmBackdropAcrylic = 3;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint DwmColorDefault = 0xFFFFFFFF;
}
