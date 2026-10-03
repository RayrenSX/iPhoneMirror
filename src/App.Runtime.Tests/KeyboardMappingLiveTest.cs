using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in hardware probe. Uses production enumeration, saved identity
    // bindings, capture and reverse-control startup. No ready state is faked.
    private static int RunKeyboardMappingLiveProbe(string output, bool exercise = false, bool wireless = false)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = main;
        main.Show();
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        bool Wait(Func<bool> completed, int seconds)
        {
            var clock = Stopwatch.StartNew();
            while (!completed() && clock.Elapsed.TotalSeconds < seconds)
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            return completed();
        }
        try
        {
            if (!Wait(() => vm.Devices.Any(d => !d.IsWireless && !d.IsMediaCast), 30))
            { Console.WriteLine("HARDWARE BLOCKED: production discovery returned no USB iPhone/iPad."); return 2; }
            var candidates = vm.Devices.Where(d => !d.IsWireless && !d.IsMediaCast &&
                vm.ResolveAppleUdid(d.Udid) is not null).ToArray();
            if (candidates.Length != 1)
            { Console.WriteLine($"HARDWARE BLOCKED: {candidates.Length} bound targets; requires one unambiguous existing binding."); return 2; }
            vm.SelectedDevice = candidates[0];
            Console.WriteLine($"HARDWARE selected: {vm.SelectedDevice.DisplayName}");
            if (vm.StartCommand.CanExecute(null)) vm.StartCommand.Execute(null);
            Wait(() => vm.SourceVideoWidth > 0 && vm.SourceVideoHeight > 0, 35);
            // The user explicitly confirmed unlock, trust and Developer Mode
            // before running this opt-in probe. This changes no phone setting.
            SetKeyboardField(vm, "_wiredControlPrerequisiteAcknowledged", true);
            SetKeyboardField(vm, "_wirelessControlPrerequisiteAcknowledged", true);
            var status = vm.GetControlStatus(wireless ? ControlStatusMode.Wireless : ControlStatusMode.Usb, vm.SelectedDevice.Udid);
            status.StatusChanged += (_, state) => Console.WriteLine($"HARDWARE control: {state.Stage} — {state.Description}");
            var start = wireless ? vm.StartWirelessControlAsync(vm.SelectedDevice.Udid) : vm.StartUsbControlAsync(vm.SelectedDevice.Udid);
            if (!Wait(() => start.IsCompleted, 180))
            { Console.WriteLine("HARDWARE BLOCKED: control startup timeout."); return 2; }
            start.GetAwaiter().GetResult();
            if (vm.GetMappingTargetStatus() != "MappingReady")
            {
                Console.WriteLine($"HARDWARE BLOCKED: {vm.GetMappingTargetStatus()}; {status.Current?.Description}; {status.Current?.Error}");
                return 2;
            }
            var path = Path.GetFullPath(Path.Combine(output, "iphone-before.png"));
            vm.CaptureScreenshot(path);
            Console.WriteLine($"HARDWARE READY: {vm.SourceVideoWidth}x{vm.SourceVideoHeight}, existing {(wireless ? "Wireless" : "USB")} mapping route ready. Frame: {path}");
            if (exercise)
            {
                // Coordinates chosen for the observed Safari test page: taps
                // in its blank upper-right area; swipes stay away from edges.
                var entries = new[]
                {
                    MappingEntry() with { X = .94, Y = .20 },
                    MappingEntry(MappedTouchAction.LongPress) with { X = .94, Y = .20, DurationMs = 650 },
                    MappingEntry(MappedTouchAction.DoubleTap) with { X = .94, Y = .20, IntervalMs = 100 },
                    MappingEntry(MappedTouchAction.SwipeUp) with { X = .9, Y = .65, Distance = .25, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.SwipeDown) with { X = .9, Y = .4, Distance = .25, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.SwipeLeft) with { X = .7, Y = .18, Distance = .2, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.SwipeRight) with { X = .5, Y = .18, Distance = .2, DurationMs = 400 },
                    MappingEntry(MappedTouchAction.Swipe) with { X = .9, Y = .65, EndX = .9, EndY = .4, DurationMs = 400 },
                };
                using var executor = new KeyboardMappingExecutor();
                foreach (var entry in entries)
                {
                    var state = new KeyboardMappingKeyState();
                    var matched = state.Process(entry.Key!, true, false, true, false, false, [entry]).Mapping!;
                    var width = vm.SourceVideoWidth; var height = vm.SourceVideoHeight;
                    var route = vm.CaptureMappingRoute(() => width == vm.SourceVideoWidth && height == vm.SourceVideoHeight,
                        (x, y) => BluetoothMouseOrientationMapper.MapNormalized(x, y, width, height, 0,
                            vm.AppliedBluetoothPortraitMouseDirection, vm.AppliedBluetoothLandscapeMouseDirection,
                            vm.AppliedBluetoothMouseReverseHorizontal, vm.AppliedBluetoothMouseReverseVertical));
                    MappingAssert(route is not null, "Live route changed before gesture.");
                    AwaitMapping(executor.ExecuteAsync(matched, route!));
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(600));
                    vm.CaptureScreenshot(Path.GetFullPath(Path.Combine(output, $"iphone-after-{entry.Action}.png")));
                    Console.WriteLine($"HARDWARE SENT: {entry.Action}, target={route!.Target}, backend={(wireless ? "Wireless" : "USB")}");
                }
            }
            else Console.WriteLine("No touch input sent by this probe.");
            return 0;
        }
        finally
        {
            var stop = vm.CancelReverseControlAsync(wireless ? ControlStatusMode.Wireless : ControlStatusMode.Usb);
            Wait(() => stop.IsCompleted, 20);
            var shutdown = vm.ShutdownAsync();
            Wait(() => shutdown.IsCompleted, 20);
            CloseWorkspaceTestWindow(main);
            Wait(() => !main.IsVisible, 15);
            app.Shutdown();
        }
    }
}
