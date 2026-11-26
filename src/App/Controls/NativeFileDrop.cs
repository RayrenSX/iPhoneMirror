using System.Runtime.InteropServices;
using System.Text;

namespace IPhoneMirror.App.Controls;

internal static class NativeFileDrop
{
    internal const int Message = 0x0233;

    internal static string[] Read(nint drop)
    {
        try
        {
            var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            if (count > 4096) return [];
            var paths = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFileW(drop, index, null, 0);
                if (length == 0 || length >= 32768) continue;
                var path = new StringBuilder((int)length + 1);
                if (DragQueryFileW(drop, index, path, length + 1) == length)
                    paths.Add(path.ToString());
            }
            return paths.ToArray();
        }
        finally { DragFinish(drop); }
    }

    [DllImport("shell32.dll")]
    internal static extern void DragAcceptFiles(nint window, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(nint drop, uint index, StringBuilder? path, uint length);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint drop);
}
