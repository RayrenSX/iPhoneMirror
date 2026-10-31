using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in hardware observation. Input comes from the real desktop/IME,
    // through the production hook, not direct calls into the keyboard router.
    private static void ObserveLiveImeKeyboard(MainWindow main, MainViewModel vm, string output)
    {
        var app = (App)Application.Current;
        var originalPreviewMode = app.IsUiPreviewMode;
        var hwnd = new WindowInteropHelper(main).Handle;
        var target = vm.SelectedDevice!.Udid;
        var host = (NativePreviewHost)main.FindName("MainPreviewHost");
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var hookField = typeof(MainWindow).GetField("_keyboardHookProc", KeyboardTestMembers)!;
        var original = (Delegate)hookField.GetValue(main)!;
        Func<int, nint, nint, nint> observer = (code, message, pointer) =>
        {
            var foreground = MappingGetForeground() == hwnd;
            var vk = code >= 0 ? Marshal.ReadInt32(pointer) : 0;
            var scan = code >= 0 ? Marshal.ReadInt32(pointer, 4) : 0;
            var flags = code >= 0 ? Marshal.ReadInt32(pointer, 8) : 0;
            var result = (nint)original.DynamicInvoke(code, message, pointer)!;
            if (code >= 0 && foreground)
                events.Enqueue($"HOOK vk={vk:X2} scan={scan:X2} flags={flags:X2} message={message:X} suppressed={result} " +
                    $"owner={((KeyboardInputRouter)KeyboardField(main, "_keyboardRouter")).Mode} layout={ImeLiveGetKeyboardLayout(0):X}");
            return result;
        };
        hookField.SetValue(main, Delegate.CreateDelegate(hookField.FieldType, observer.Target, observer.Method));
        app.IsUiPreviewMode = false;
        KeyboardCall(main, "ReconcileKeyboardHook");
        main.Title = "iPhoneMirror — Sogou Chinese IME hardware verification";
        ActivateMappingTestWindow(main);
        KeyboardCall(main, "FocusControlDevice", target, (nint)0);
        KeyboardCall(main, "TryEnterDirectKeyboardInputMode");
        host.Focus();
        MappingSetFocus(host.WindowHandle);
        Console.WriteLine("IME LIVE READY: production USB capture/control; use the observed phone search field. No synthetic router callbacks.");
        var clock = Stopwatch.StartNew();
        try
        {
            while (main.IsVisible && clock.Elapsed < TimeSpan.FromMinutes(15) && !File.Exists(Path.Combine(output, "stop")))
            {
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                while (events.TryDequeue(out var line)) Console.WriteLine(line);
                var inputRequest = Path.Combine(output, "key-sequence.txt");
                if (File.Exists(inputRequest))
                {
                    var sequence = File.ReadAllText(inputRequest).Trim();
                    File.Delete(inputRequest);
                    MappingAssert(MappingGetForeground() == hwnd &&
                        (bool)KeyboardCall(main, "CanForwardControlKeyboard", target, hwnd)!,
                        "Physical scan-code test requires the foreground device preview.");
                    ushort[] scans = sequence switch
                    {
                        "nihao" => [0x31, 0x17, 0x23, 0x1E, 0x18],
                        "abc123" => [0x1E, 0x30, 0x2E, 0x02, 0x03, 0x04],
                        "backspace" => [0x0E],
                        "space" => [0x39],
                        "left" => [0x14B],
                        "shift-a" => [0x1E],
                        _ => throw new InvalidOperationException("Unknown physical input test sequence."),
                    };
                    void Send(ushort scan, bool down)
                    {
                        var input = new ImeLiveScanInput { Type = 1, Scan = (ushort)(scan & 0xFF),
                            Flags = 8u | ((scan & 0x100) != 0 ? 1u : 0u) | (down ? 0u : 2u) };
                        MappingAssert(ImeLiveSend(1, [input], 40) == 1,
                            $"Physical scan-code SendInput failed: {Marshal.GetLastWin32Error()}");
                    }
                    var typing = Task.Run(() =>
                    {
                        if (sequence == "shift-a") { Send(0x2A, true); Thread.Sleep(50); }
                        try
                        {
                            foreach (var scan in scans)
                            {
                                MappingAssert(MappingGetForeground() == hwnd, "Preview lost foreground during scan-code input.");
                                Send(scan, true);
                                try { Thread.Sleep(60); }
                                finally { Send(scan, false); }
                                Thread.Sleep(90);
                            }
                        }
                        finally { if (sequence == "shift-a") Send(0x2A, false); }
                    });
                    AwaitMapping(typing);
                    Console.WriteLine($"SCAN INPUT completed: {sequence}; real Windows input, no VK_PACKET or router injection.");
                }
                var request = Path.Combine(output, "capture-request.txt");
                if (File.Exists(request))
                {
                    var name = File.ReadAllText(request).Trim();
                    File.Delete(request);
                    if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                    {
                        var path = Path.GetFullPath(Path.Combine(output, name + ".png"));
                        vm.CaptureScreenshot(path);
                        var focus = ImeLiveGetFocus();
                        var context = ImeLiveGetContext(focus);
                        try
                        {
                            var conversion = 0u;
                            var sentence = 0u;
                            if (context != 0) ImeLiveGetConversionStatus(context, out conversion, out sentence);
                            Console.WriteLine($"FRAME {name}: layout={ImeLiveGetKeyboardLayout(0):X}, focus={focus}, " +
                                $"ime_open={(context != 0 && ImeLiveGetOpenStatus(context))}, conversion={conversion:X}, frame={path}");
                        }
                        finally { if (context != 0) ImeLiveReleaseContext(focus, context); }
                    }
                }
            }
        }
        finally
        {
            app.IsUiPreviewMode = originalPreviewMode;
            KeyboardCall(main, "ReconcileKeyboardHook");
            hookField.SetValue(main, original);
            GC.KeepAlive(observer);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct ImeLiveScanInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(10)] internal ushort Scan;
        [FieldOffset(12)] internal uint Flags;
    }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint ImeLiveSend(uint count, ImeLiveScanInput[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout")]
    private static extern nint ImeLiveGetKeyboardLayout(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetFocus")]
    private static extern nint ImeLiveGetFocus();
    [DllImport("imm32.dll", EntryPoint = "ImmGetContext")]
    private static extern nint ImeLiveGetContext(nint window);
    [DllImport("imm32.dll", EntryPoint = "ImmReleaseContext")]
    private static extern bool ImeLiveReleaseContext(nint window, nint context);
    [DllImport("imm32.dll", EntryPoint = "ImmGetOpenStatus")]
    private static extern bool ImeLiveGetOpenStatus(nint context);
    [DllImport("imm32.dll", EntryPoint = "ImmGetConversionStatus")]
    private static extern bool ImeLiveGetConversionStatus(nint context, out uint conversion, out uint sentence);
}
