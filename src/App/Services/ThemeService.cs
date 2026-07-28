using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Updater;
using Microsoft.Win32;

namespace IPhoneMirror.App.Services;

internal static class ThemeService
{
    private static readonly ConditionalWeakTable<Window, object> AttachedWindows = new();
    private static readonly IReadOnlyDictionary<string, string> DarkColors =
        new Dictionary<string, string>
        {
            ["BackgroundBrush"] = "#202020",
            ["AppBackgroundBrush"] = "#CC202020",
            ["PanelBrush"] = "#D9262626",
            ["PanelAltBrush"] = "#E62D2D2D",
            ["PanelRaisedBrush"] = "#E62A2A2A",
            ["BorderBrush"] = "#4B4B4B",
            ["BorderSoftBrush"] = "#3A3A3A",
            ["TextBrush"] = "#F7F7F7",
            ["MutedTextBrush"] = "#B8B8B8",
            ["AccentBrush"] = "#60CDFF",
            ["AccentHoverBrush"] = "#99EBFF",
            ["OnAccentBrush"] = "#002C3D",
            ["SuccessBrush"] = "#7BD88F",
            ["WarningBrush"] = "#F4C95D",
            ["StatusAppliedBrush"] = "#7BD88F",
            ["StatusPendingBrush"] = "#F4C95D",
            ["StatusFailedBrush"] = "#FF7B72",
            ["ComboBackgroundBrush"] = "#D9252525",
            ["ComboHoverBrush"] = "#353535",
            ["ComboOpenBrush"] = "#3A3A3A",
            ["ComboPopupBrush"] = "#2B2B2B",
            ["ComboItemHoverBrush"] = "#3A3A3A",
            ["ComboItemSelectedBrush"] = "#454545",
            ["ComboItemSelectedHoverBrush"] = "#505050",
            ["ComboBorderHoverBrush"] = "#727272",
            ["ScrollTrackBrush"] = "#292929",
            ["ScrollThumbBrush"] = "#666666",
            ["ScrollThumbHoverBrush"] = "#898989",
        };

    private static readonly IReadOnlyDictionary<string, string> LightColors =
        new Dictionary<string, string>
        {
            ["BackgroundBrush"] = "#F3F3F3",
            ["AppBackgroundBrush"] = "#E6F3F3F3",
            ["PanelBrush"] = "#EFFFFFFF",
            ["PanelAltBrush"] = "#F7FFFFFF",
            ["PanelRaisedBrush"] = "#FFFFFFFF",
            ["BorderBrush"] = "#D0D0D0",
            ["BorderSoftBrush"] = "#DDDDDD",
            ["TextBrush"] = "#1B1B1B",
            ["MutedTextBrush"] = "#5E5E5E",
            ["AccentBrush"] = "#0067C0",
            ["AccentHoverBrush"] = "#005A9E",
            ["OnAccentBrush"] = "#FFFFFF",
            ["SuccessBrush"] = "#0F7B3E",
            ["WarningBrush"] = "#9A6700",
            ["StatusAppliedBrush"] = "#0F7B3E",
            ["StatusPendingBrush"] = "#9A6700",
            ["StatusFailedBrush"] = "#C42B1C",
            ["ComboBackgroundBrush"] = "#FFFFFFFF",
            ["ComboHoverBrush"] = "#F0F0F0",
            ["ComboOpenBrush"] = "#EAEAEA",
            ["ComboPopupBrush"] = "#FFFFFFFF",
            ["ComboItemHoverBrush"] = "#F0F0F0",
            ["ComboItemSelectedBrush"] = "#E5F1FB",
            ["ComboItemSelectedHoverBrush"] = "#D8EAF8",
            ["ComboBorderHoverBrush"] = "#8A8A8A",
            ["ScrollTrackBrush"] = "#E8E8E8",
            ["ScrollThumbBrush"] = "#8A8A8A",
            ["ScrollThumbHoverBrush"] = "#666666",
        };

    internal static bool IsDark { get; private set; } = true;

    internal static void Apply(AppTheme theme)
    {
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };
        var application = Application.Current;
        if (application is null) return;
        foreach (var pair in IsDark ? DarkColors : LightColors)
        {
            var color = (Color)ColorConverter.ConvertFromString(pair.Value);
            if (application.Resources[pair.Key] is SolidColorBrush brush &&
                !brush.IsFrozen)
                brush.Color = color;
            else
                application.Resources[pair.Key] = new SolidColorBrush(color);
        }
        foreach (Window window in application.Windows)
            ApplyBackdrop(window);
    }

    internal static void Attach(Window window)
    {
        if (AttachedWindows.TryGetValue(window, out _)) return;
        AttachedWindows.Add(window, new object());
        if (!window.AllowsTransparency)
            window.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
        window.SourceInitialized += (_, _) => ApplyBackdrop(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyBackdrop(window);
    }

    private static void ApplyBackdrop(Window window)
    {
        if (window.AllowsTransparency) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        var corner = 2;
        var backdrop = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
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
        catch { return true; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
        ref int value, int valueSize);
}
