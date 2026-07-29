using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Services;

internal static class AppIdentity
{
    internal const string AppUserModelId = "RayrenSX.iPhoneMirror";
    private const uint SemFailCriticalErrors = 0x0001;

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

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
