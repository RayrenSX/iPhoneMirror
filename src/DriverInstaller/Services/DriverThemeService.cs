using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace IPhoneMirror.DriverInstaller.Services;

public enum DriverThemeMode
{
    System,
    Light,
    Dark,
}

internal static class DriverThemeService
{
    private sealed record ThemeSettings(DriverThemeMode Theme);

    private sealed class WindowBackdropState
    {
        internal bool EdgeToEdge { get; set; }
    }

    private const string LightThemePath = "Themes/LightTheme.xaml";
    private const string DarkThemePath = "Themes/DarkTheme.xaml";
    private static readonly ConditionalWeakTable<Window, WindowBackdropState> AttachedWindows = new();
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "iPhoneMirror.Driver", "ui-settings.json");
    private static bool _systemEventsAttached;

    internal static DriverThemeMode Preference { get; private set; } = DriverThemeMode.System;
    internal static bool IsDark { get; private set; } = true;

    internal static void Initialize(IReadOnlyList<string>? arguments = null)
    {
        if (TryReadThemeArgument(arguments, out var launchTheme))
        {
            Apply(launchTheme, persist: false);
            return;
        }

        try
        {
            if (File.Exists(SettingsPath))
                Preference = JsonSerializer.Deserialize<ThemeSettings>(
                    File.ReadAllText(SettingsPath))?.Theme ?? DriverThemeMode.System;
        }
        catch (Exception error) when (error is IOException or JsonException or
                                      UnauthorizedAccessException)
        {
            DriverLogger.WriteException("theme", "settings_load_failed", error);
            Preference = DriverThemeMode.System;
        }
        Apply(Preference, persist: false);
    }

    private static bool TryReadThemeArgument(IReadOnlyList<string>? arguments,
        out DriverThemeMode theme)
    {
        theme = DriverThemeMode.System;
        if (arguments is null) return false;
        for (var index = 0; index + 1 < arguments.Count; ++index)
        {
            if (!string.Equals(arguments[index], "--theme",
                    StringComparison.OrdinalIgnoreCase) ||
                !Enum.TryParse(arguments[index + 1], ignoreCase: true, out theme))
                continue;
            return true;
        }
        return false;
    }

    internal static void Apply(DriverThemeMode theme, bool persist = true)
    {
        Preference = theme;
        IsDark = theme switch
        {
            DriverThemeMode.Dark => true,
            DriverThemeMode.Light => false,
            _ => IsSystemDark(),
        };
        EnsureSystemEvents();
        var application = Application.Current;
        if (application is not null)
        {
            SwapThemeDictionary(application.Resources,
                IsDark ? DarkThemePath : LightThemePath);
            foreach (Window window in application.Windows)
            {
                ApplyBackdrop(window);
                if (window.IsLoaded)
                    window.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0.88, 1,
                            TimeSpan.FromMilliseconds(180)));
            }
        }
        if (persist) Save();
    }

    internal static void Attach(Window window)
    {
        if (AttachedWindows.TryGetValue(window, out _)) return;
        AttachedWindows.Add(window, new WindowBackdropState());
        if (!window.AllowsTransparency)
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SourceInitialized += (_, _) => ApplyBackdrop(window);
        window.StateChanged += (_, _) => ApplyBackdrop(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyBackdrop(window);
    }

    internal static void Shutdown()
    {
        if (!_systemEventsAttached) return;
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _systemEventsAttached = false;
    }

    private static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(new ThemeSettings(Preference)));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DriverLogger.WriteException("theme", "settings_save_failed", error);
        }
    }

    private static void SwapThemeDictionary(ResourceDictionary resources, string path)
    {
        var existing = resources.MergedDictionaries.FirstOrDefault(dictionary =>
        {
            var value = dictionary.Source?.OriginalString;
            return value?.EndsWith(LightThemePath,
                       StringComparison.OrdinalIgnoreCase) == true ||
                   value?.EndsWith(DarkThemePath,
                       StringComparison.OrdinalIgnoreCase) == true;
        });
        if (existing?.Source?.OriginalString.EndsWith(path,
                StringComparison.OrdinalIgnoreCase) == true)
            return;
        var replacement = new ResourceDictionary
        {
            Source = new Uri(path, UriKind.Relative),
        };
        if (existing is null) resources.MergedDictionaries.Insert(0, replacement);
        else resources.MergedDictionaries[resources.MergedDictionaries.IndexOf(existing)] =
            replacement;
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
        if (Preference != DriverThemeMode.System || Application.Current is null) return;
        _ = Application.Current.Dispatcher.BeginInvoke(() =>
            Apply(DriverThemeMode.System, persist: false));
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
            DriverLogger.WriteException("theme", "system_theme_read_failed", error);
            return true;
        }
    }

    private static void ApplyBackdrop(Window window)
    {
        if (window.AllowsTransparency ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        _ = AttachedWindows.TryGetValue(window, out var state);
        var flushToDisplayEdge = state?.EdgeToEdge == true ||
            window.WindowState == WindowState.Maximized;
        var dark = IsDark ? 1 : 0;
        var corner = flushToDisplayEdge ? DwmDoNotRound : DwmRound;
        var backdrop = DwmBackdropMica;
        var border = flushToDisplayEdge ? DwmColorNone : DwmColorDefault;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, sizeof(int));
        _ = DwmSetWindowAttributeColor(handle, DwmBorderColor, ref border, sizeof(uint));
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
    private const int DwmBackdropMica = 2;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint DwmColorDefault = 0xFFFFFFFF;
}
