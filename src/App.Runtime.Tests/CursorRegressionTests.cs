using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestReverseControlCursorShapes()
    {
        var assembly = typeof(App).Assembly;
        var hostType = assembly.GetType("IPhoneMirror.App.Controls.NativePreviewHost", true)!;
        using var host = (IDisposable)Activator.CreateInstance(hostType, nonPublic: true)!;
        hostType.GetProperty("CapturePointerInput", InteractionMembers)!.SetValue(host, true);

        // Only exercise cursor negotiation: no renderer, device connection or
        // visible preview window is needed for this message handler.
        var previewType = assembly.GetType("IPhoneMirror.App.Windows.NativePreviewWindow", true)!;
        var preview = RuntimeHelpers.GetUninitializedObject(previewType);
        var pointerField = previewType.GetField("_pointerInput", InteractionMembers)!;
        var pointerParameter = Expression.Parameter(pointerField.FieldType.GenericTypeArguments[0]);
        pointerField.SetValue(preview, Expression.Lambda(pointerField.FieldType,
            Expression.Empty(), pointerParameter).Compile());
        InteractionSet(preview, "_isUsbControlEnabled", (Func<bool>)(() => true));

        var originalCursor = CursorTestGetCursor();
        var originalCount = ReadCursorDisplayCount();
        var arrow = CursorTestLoadCursor(0, 32512); // IDC_ARROW
        InteractionAssert(arrow != 0, "Load system arrow cursor");
        try
        {
            foreach (var cursorId in new[] { 32644, 32645, 32642, 32643, 32649, 32513 })
            {
                var stale = CursorTestLoadCursor(0, cursorId);
                InteractionAssert(stale != 0, $"Load stale cursor {cursorId}");
                foreach (var target in new[] { (Owner: (object)host, Method: "WndProc"),
                    (Owner: preview, Method: "WindowProcedure") })
                {
                    CursorTestSetCursor(stale);
                    object[] args = [(nint)0, 0x0020, (nint)0, (nint)1, false];
                    var result = target.Owner.GetType().GetMethod(target.Method, InteractionMembers)!
                        .Invoke(target.Owner, args);
                    InteractionAssert((bool)args[4] && (nint)result! == 1 && CursorTestGetCursor() == arrow,
                        $"{target.Method} must replace cursor {cursorId} with the system arrow");
                }
            }

            while (CursorTestShowCursor(false) >= 0) { }
            var hiddenCount = ReadCursorDisplayCount();
            for (var index = 0; index < 20; index++)
            {
                CursorTestSetCursor(CursorTestLoadCursor(0, 32649));
                object[] args = [(nint)0, 0x0020, (nint)0, (nint)1, false];
                hostType.GetMethod("WndProc", InteractionMembers)!.Invoke(host, args);
                InteractionAssert(CursorTestGetCursor() == arrow && ReadCursorDisplayCount() == hiddenCount,
                    "Repeated arrow updates must preserve Bluetooth's hidden cursor count");
            }

            InteractionSet(preview, "_isUsbControlEnabled", (Func<bool>)(() => false));
            var hand = CursorTestLoadCursor(0, 32649);
            CursorTestSetCursor(hand);
            object[] inactiveArgs = [(nint)0, 0x0020, (nint)0, (nint)1, false];
            previewType.GetMethod("WindowProcedure", InteractionMembers)!.Invoke(preview, inactiveArgs);
            InteractionAssert(!(bool)inactiveArgs[4] && CursorTestGetCursor() == hand,
                "Inactive preview must leave normal cursor negotiation to Windows");
        }
        finally
        {
            var count = ReadCursorDisplayCount();
            while (count < originalCount) count = CursorTestShowCursor(true);
            while (count > originalCount) count = CursorTestShowCursor(false);
            CursorTestSetCursor(originalCursor);
        }
        Console.WriteLine("Cursor regression: six stale shapes on both previews, repeated hidden updates and inactive fallback passed.");
    }

    private static int ReadCursorDisplayCount()
    {
        CursorTestShowCursor(true);
        return CursorTestShowCursor(false);
    }

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint CursorTestLoadCursor(nint instance, nint cursor);
    [DllImport("user32.dll", EntryPoint = "SetCursor")]
    private static extern nint CursorTestSetCursor(nint cursor);
    [DllImport("user32.dll", EntryPoint = "GetCursor")]
    private static extern nint CursorTestGetCursor();
    [DllImport("user32.dll", EntryPoint = "ShowCursor")]
    private static extern int CursorTestShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
}
