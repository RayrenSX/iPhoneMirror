using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Services;

internal static class AppIdentity
{
    internal const string AppUserModelId = "RayrenSX.iPhoneMirror";
    private const uint SemFailCriticalErrors = 0x0001;

    internal static void Initialize()
    {
        try { SetErrorMode(GetErrorMode() | SemFailCriticalErrors); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
