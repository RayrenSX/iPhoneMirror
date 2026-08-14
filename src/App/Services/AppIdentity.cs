using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IPhoneMirror.App.Services;

internal static class AppIdentity
{
    internal const string AppUserModelId = "RayrenSX.iPhoneMirror";
    private const uint SemFailCriticalErrors = 0x0001;
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    internal static void Initialize()
    {
        try { SetErrorMode(GetErrorMode() | SemFailCriticalErrors); }
        catch (Exception error) when (error is DllNotFoundException or
                                      EntryPointNotFoundException)
        {
            DiagnosticLogger.Exception("identity", "set_error_mode_failed", error);
        }

        try
        {
            var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (result != 0)
                DiagnosticLogger.Error("identity", "app_user_model_id_failed",
                    ("hresult", $"0x{result:X8}"));
        }
        catch (Exception error) when (error is DllNotFoundException or
                                      EntryPointNotFoundException)
        {
            DiagnosticLogger.Exception("identity", "app_user_model_id_exception", error);
        }
    }

    internal static void Attach(Window window)
    {
        nint largeIcon = 0;
        nint smallIcon = 0;

        void ApplyOnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= ApplyOnSourceInitialized;
            var handle = new WindowInteropHelper(window).Handle;
            var executable = Environment.ProcessPath;
            if (handle == 0 || string.IsNullOrWhiteSpace(executable) ||
                ExtractIconExW(executable, 0, out largeIcon, out smallIcon, 1) == 0)
                return;

            if (largeIcon != 0)
                _ = SendMessageW(handle, WmSetIcon, IconBig, largeIcon);
            if (smallIcon != 0)
                _ = SendMessageW(handle, WmSetIcon, IconSmall, smallIcon);
            window.Closed += ReleaseIcons;
        }

        void ReleaseIcons(object? sender, EventArgs args)
        {
            window.Closed -= ReleaseIcons;
            if (largeIcon != 0) _ = DestroyIcon(largeIcon);
            if (smallIcon != 0 && smallIcon != largeIcon) _ = DestroyIcon(smallIcon);
            largeIcon = 0;
            smallIcon = 0;
        }

        window.SourceInitialized += ApplyOnSourceInitialized;
        if (new WindowInteropHelper(window).Handle != 0)
            ApplyOnSourceInitialized(window, EventArgs.Empty);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int iconIndex,
        out nint largeIcon, out nint smallIcon, uint iconCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint window, int message,
        nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
