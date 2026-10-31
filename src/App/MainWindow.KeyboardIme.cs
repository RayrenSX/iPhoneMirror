using System.Runtime.InteropServices;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private static int RestoreImeVirtualKey(int virtualKey, int scanCode)
    {
        // Third-party IMEs can replace the virtual key with VK_PROCESSKEY.
        // Recover only that sentinel; remapping ordinary keys would break
        // NumLock and layouts whose virtual keys already carry useful state.
        if (virtualKey != 0xE5 || (scanCode & 0xFF) == 0) return virtualKey;
        var scan = (uint)(scanCode & 0xFF) | ((scanCode & 0x100) != 0 ? 0xE000u : 0);
        var restored = (int)MapImeScanCode(scan, 3); // MAPVK_VSC_TO_VK_EX keeps left/right modifiers.
        return restored is > 0 and < 0xFF && restored is not (0xE5 or 0xE7)
            ? restored : virtualKey;
    }

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)]
    private static extern uint MapImeScanCode(uint code, uint mapType);
}
