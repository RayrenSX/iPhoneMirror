using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void ExerciseLiveClipboard(MainWindow main, MainViewModel vm, string target,
        string label, Action focus, Action clear, Action<string> frame, Action<int, bool> routed)
    {
        // Save clipboard formats in memory only. Never write the user's prior
        // clipboard to the test log or evidence files.
        var saved = new DataObject();
        var original = Clipboard.GetDataObject();
        if (original is not null)
            foreach (var format in original.GetFormats(autoConvert: false))
            {
                var value = original.GetData(format, autoConvert: false);
                if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
                if (value is not null) saved.SetData(format, value, autoConvert: false);
            }
        var token = $"clip-{label} 中文 café";
        var secondToken = $"return-{label} 双向 😀";
        var phoneText = "phonecopy";
        var sentinel = $"local-newer-{label}";
        var known = new HashSet<string> { token, secondToken, phoneText, sentinel };
        void WaitForClipboard(string expected)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(12))
            {
                try { if (Clipboard.GetText() == expected) return; }
                catch (System.Runtime.InteropServices.ExternalException) { }
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            }
            throw new InvalidOperationException($"{label}: expected test clipboard text did not arrive from the phone.");
        }
        void Shortcut(int key)
        {
            focus();
            routed(0xA2, true); routed(key, true);
            for (var repeat = 0; repeat < 20; repeat++) routed(key, true);
            routed(key, false); routed(0xA2, false);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            AwaitMapping(Task.WhenAll((HashSet<Task>)KeyboardField(main, "_keyboardSends")));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(900));
            MappingAssert(((KeyboardInputRouter)KeyboardField(main, "_keyboardRouter")).Mode == KeyboardInputMode.Mapping,
                "Clipboard action disabled mapping mode.");
        }
        void NativeCommand(byte usage)
        {
            // Test setup for selection / a copy performed on the phone while
            // Windows has focus elsewhere. This bypasses desktop key routing.
            var route = vm.CaptureDirectKeyboardRoute(target)!;
            foreach (var keys in new byte[][] { [0xE3], [0xE3, usage], [0xE3], [] })
            {
                AwaitMapping(route.SendAsync(0, keys, () => true));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            }
        }
        try
        {
            clear();
            Clipboard.SetText(token);
            Shortcut(0x56);
            frame(label + "-clipboard-pasted");
            focus(); NativeCommand(0x04); // select all in the observed search field
            Clipboard.SetText(sentinel);
            Shortcut(0x43);
            WaitForClipboard(token);
            Console.WriteLine($"LIVE CLIPBOARD {label}: mapped C/V coexist with Ctrl+V and Ctrl+C; exact Unicode roundtrip passed (20 repeat downs each).");

            // Force another copy of unchanged phone text after a new local
            // copy. A text-change-only polling baseline would miss this.
            Clipboard.SetText(sentinel);
            Shortcut(0x43); WaitForClipboard(token);
            Console.WriteLine($"LIVE CLIPBOARD {label}: repeated same phone copy replaces a newer Windows test copy.");

            clear();
            var route = vm.CaptureDirectKeyboardRoute(target)!;
            foreach (var letter in phoneText)
            {
                AwaitMapping(route.SendAsync(0, [(byte)(letter - 'a' + 4)], () => true));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(70));
                AwaitMapping(route.SendAsync(0, [], () => true));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(70));
            }
            NativeCommand(0x04);
            var editor = new TextBox { AcceptsReturn = true, Margin = new Thickness(16) };
            var receiver = new Window { Title = "Clipboard roundtrip verification", Width = 480, Height = 180,
                Content = editor, ShowInTaskbar = true };
            try
            {
                receiver.Show(); ActivateMappingTestWindow(receiver); editor.Focus();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
                NativeCommand(0x06); // independent device-side Copy, desktop remains in the receiver
                WaitForClipboard(phoneText);
                ApplicationCommands.Paste.Execute(null, editor);
                MappingAssert(editor.Text == phoneText, "Windows could not paste the phone's background clipboard update.");
                editor.Text = secondToken; editor.SelectAll();
                ApplicationCommands.Copy.Execute(null, editor);
                WaitForClipboard(secondToken);
            }
            finally { receiver.Close(); }
            focus(); NativeCommand(0x04); Shortcut(0x56);
            frame(label + "-clipboard-returned");
            focus(); NativeCommand(0x04);
            Clipboard.SetText(sentinel); Shortcut(0x43); WaitForClipboard(secondToken);
            Console.WriteLine($"LIVE CLIPBOARD {label}: background phone copy -> Windows editor paste -> Windows copy -> mapped-mode phone paste passed.");
            clear();
        }
        finally
        {
            // Respect a new user copy made during the test instead of restoring
            // over it. The saved clipboard never leaves this process.
            if (known.Contains(Clipboard.GetText())) Clipboard.SetDataObject(saved, copy: true);
        }
    }
}
