using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Services;

internal static class AppIdentity
{
    internal const string AppUserModelId = "RayrenSX.iPhoneMirror";

    internal static void Initialize()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
