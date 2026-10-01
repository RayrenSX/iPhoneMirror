using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IPhoneMirror.App.Services;

/// <summary>A hidden top-level HWND receives both shell callbacks and Explorer restarts.</summary>
internal sealed class TrayIconService : IDisposable
{
    private const int CallbackMessage = 0x8000 + 91;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly Action _open;
    private NotifyIconData _data;
    private bool _disposed;

    internal TrayIconService(Action open)
    {
        _open = open;
        _source = new HwndSource(new HwndSourceParameters("iPhoneMirror.Tray")
        {
            WindowStyle = unchecked((int)0x80000000),
            Width = 0,
            Height = 0,
        });
        _source.AddHook(WindowProcedure);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _source.Handle,
            Id = 1,
            Flags = 1 | 2 | 4 | 0x80, // message, icon, tooltip, show tooltip under v4
            Callback = CallbackMessage,
            Icon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "Assets", "iPhoneMirror.ico"),
                1, 0, 0, 0x10 | 0x40),
            Tip = "iPhoneMirror",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
        if (_data.Icon == 0 || !AddIcon())
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    private bool AddIcon()
    {
        if (!Shell_NotifyIconW(0, ref _data)) return false;
        _data.Version = 4;
        Shell_NotifyIconW(4, ref _data);
        return true;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if ((uint)message == _taskbarCreated)
        {
            if (!AddIcon()) DiagnosticLogger.Info("tray", "icon_restore_failed");
        }
        if (message == CallbackMessage)
        {
            var notification = (int)(lParam.ToInt64() & 0xffff);
            // NIN_SELECT / NIN_KEYSELECT, or the shell's context menu request.
            if (notification is 0x400 or 0x401 or 0x7b)
                _source.Dispatcher.BeginInvoke(_open);
            handled = true;
        }
        return 0;
    }

    internal void ShowPanel(Window panel)
    {
        // Position in physical pixels on the monitor containing the tray icon;
        // this also handles secondary monitors, taskbar edges and mixed DPI.
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _source.Handle, Id = 1,
        };
        NativePoint anchor;
        if (Shell_NotifyIconGetRect(ref identifier, out var iconBounds) == 0)
            anchor = new NativePoint { X = (iconBounds.Left + iconBounds.Right) / 2,
                Y = (iconBounds.Top + iconBounds.Bottom) / 2 };
        else GetCursorPos(out anchor);
        var monitor = MonitorFromPoint(anchor, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var handle = new WindowInteropHelper(panel).EnsureHandle();
        // Move the hidden HWND first so WPF obtains this monitor's actual DPI.
        SetWindowPos(handle, 0, anchor.X, anchor.Y, 0, 0, 0x15);
        var dpi = Math.Max(96u, GetDpiForWindow(handle)) / 96d;
        var width = (int)Math.Ceiling(Math.Min(panel.Width, (info.Work.Right - info.Work.Left) / dpi) * dpi);
        var height = (int)Math.Ceiling(Math.Min(panel.Height, (info.Work.Bottom - info.Work.Top) / dpi) * dpi);
        var left = Math.Clamp(anchor.X - width / 2, info.Work.Left, info.Work.Right - width);
        var top = Math.Clamp(anchor.Y - height, info.Work.Top, info.Work.Bottom - height);
        SetWindowPos(handle, 0, left, top, width, height, 0x14);
        SetForegroundWindow(handle);
        panel.Show();
        panel.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_data.Window != 0) Shell_NotifyIconW(2, ref _data);
        if (_data.Icon != 0) DestroyIcon(_data.Icon);
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier
    { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint operation, ref NotifyIconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
