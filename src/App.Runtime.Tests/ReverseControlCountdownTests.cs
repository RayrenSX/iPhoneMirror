using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

// Exercise the production status service, XAML window and timers on a real STA
// dispatcher. Only the transport's status reports are simulated.
internal static class ReverseControlCountdownTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly Assembly = typeof(App).Assembly;
    private static readonly Type ServiceType = Type("ControlStatusService");
    private static readonly Type ModeType = Type("ControlStatusMode");
    private static readonly Type StageType = Type("ControlStage");
    private static Type Type(string name) => Assembly.GetType($"IPhoneMirror.App.Services.{name}", true)!;

    internal static int Run()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", Instance)!.SetValue(app, true);
        app.InitializeComponent();
        var owner = new Wpf.Ui.Controls.FluentWindow { Width = 320, Height = 240, ShowInTaskbar = false };
        owner.Show();
        try
        {
            Run(owner);
            return 0;
        }
        finally
        {
            owner.Close();
            app.Shutdown();
        }
    }

    internal static void Run(Window owner)
    {
        TestIndependentNativeOwnerRoute(owner);
        foreach (var mode in new[] { "Usb", "Wireless", "Bluetooth" })
            TestQueuedSuccess(owner, mode);
        TestQueuedSuccess(owner, "Usb", workerReports: true);
        TestDisplayTimerCompletesCountdown(owner);
        TestFailure(owner);
        TestPromptAndRecovery(owner);
        TestManualCloseAndReplacement(owner);
        TestForegroundCloseReturnsToOwner(owner, "Usb");
        TestForegroundCloseReturnsToOwner(owner, "Wireless");
        TestBackgroundCountdownPreservesFocus(owner);
        TestClosePreservesUnavailableOwner(owner, minimized: false);
        TestClosePreservesUnavailableOwner(owner, minimized: true);
        Console.WriteLine("Reverse-control countdown runtime tests passed.");
    }

    private static void TestIndependentNativeOwnerRoute(Window originalOwner)
    {
        var app = Application.Current;
        var previousMain = app.MainWindow;
        var main = Program.CreateWorkspaceTestWindow(app, includeNativePreview: false);
        using var native = new HwndSource(new HwndSourceParameters("Independent control owner fixture")
        { WindowStyle = 0x10CF0000, Width = 240, Height = 340, PositionX = 800, PositionY = 120 });
        Window? status = null;
        try
        {
            // This is the real independent-window entry. A missing device exits
            // after showing status, before any transport or hardware is started.
            typeof(MainWindow).GetMethod("OnIndependentUsbControlRequested", Instance)!
                .Invoke(main, ["native-owner-fixture-missing-device", native.Handle]);
            status = app.Windows.OfType<ReverseControlStatusWindow>().Single();
            var statusHandle = new WindowInteropHelper(status).Handle;
            Require(GetWindow(statusHandle, 4) == native.Handle,
                $"Independent control status must belong to its native preview; expected={native.Handle}, actual={GetWindow(statusHandle, 4)}.");
            var service = status.DataContext.GetType().GetField("_service", Instance)!.GetValue(status.DataContext)!;
            Require(ReferenceEquals(status, Show(main, service)), "VM continuation must reuse the independent status window.");
            Require(GetWindow(statusHandle, 4) == native.Handle,
                "A later VM Show(main, service) must preserve the independent native owner.");
            main.WindowState = WindowState.Minimized;
            Pump(TimeSpan.FromMilliseconds(80));
            Require(IsWindowVisible(statusHandle) && !IsIconic(statusHandle),
                "Minimizing main must not minimize an independent preview's control prompt.");
            main.Hide();
            Pump(TimeSpan.FromMilliseconds(80));
            Require(IsWindowVisible(statusHandle), "Hiding main must not hide the independent control prompt.");
            var mainStateBeforeClose = main.WindowState;
            var mainVisibleBeforeClose = main.IsVisible;
            ActivateForeground(status);
            Call(status, "OnCloseClick", status, new RoutedEventArgs());
            var foregroundAfterClose = GetForegroundWindow();
            Require(!status.IsVisible && foregroundAfterClose == native.Handle,
                $"Closing independent status must return to its native preview; expected={native.Handle}, actual={foregroundAfterClose}.");
            Require(main.IsVisible == mainVisibleBeforeClose && main.WindowState == mainStateBeforeClose,
                "Closing independent status must preserve main's actual hidden/minimized state before close.");
            status = Show(main, service);
            Require(GetWindow(new WindowInteropHelper(status).Handle, 4) == native.Handle,
                "Closing a status window must retain the live native route for later failure/recovery.");
            Call(service, "Ready", Enum.Parse(ModeType, "Usb"), "Native countdown fixture", "Connected");
            Pump(TimeSpan.FromMilliseconds(100));
            // The full five-second timing is covered in TestQueuedSuccess. Keep
            // this focus test in a single foreground turn so unrelated desktop
            // activity cannot switch it to the background countdown case.
            var countdownStart = status.DataContext.GetType().GetField("_countdownStartedAt", Instance)!;
            var startedAt = (long)countdownStart.GetValue(status.DataContext)!;
            Require(startedAt != 0 && Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(5),
                "Native-owner countdown must be armed before simulating its elapsed deadline.");
            ActivateForeground(status);
            countdownStart.SetValue(status.DataContext, Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency);
            Call(status.DataContext, "OnCountdownDeadline");
            Require(!status.IsVisible && GetForegroundWindow() == native.Handle &&
                main.IsVisible == mainVisibleBeforeClose && main.WindowState == mainStateBeforeClose,
                "The real native-owner countdown must close, return focus to its preview and preserve main state.");
            Console.WriteLine("PASS: native-owner countdown returns focus without restoring main.");
            Call(service, "Begin", Enum.Parse(ModeType, "Usb"), "Native owner fixture");
            status = Show(main, service);
            var cover = new Window { Title = "Independent status background fixture", Width = 240, Height = 160,
                ShowInTaskbar = false, Style = null };
            try
            {
                cover.Show(); ActivateForeground(cover);
                Require(cover.IsActive && !status.IsActive, "Independent background-close fixture must activate another window.");
                Call(status, "OnCloseClick", status, new RoutedEventArgs());
                Pump(TimeSpan.FromMilliseconds(80));
                Require(cover.IsActive && GetForegroundWindow() == new WindowInteropHelper(cover).Handle,
                    "Closing background native-owned status must preserve foreground focus.");
            }
            finally { cover.Close(); }
            foreach (var minimize in new[] { false, true })
            {
                status = Show(main, service);
                ShowWindow(native.Handle, minimize ? 6 : 0);
                Pump(TimeSpan.FromMilliseconds(80));
                Call(status, "OnCloseClick", status, new RoutedEventArgs());
                Pump(TimeSpan.FromMilliseconds(80));
                Require(minimize ? IsIconic(native.Handle) : !IsWindowVisible(native.Handle),
                    "Closing independent status must preserve its native owner's hidden/minimized state.");
                ShowWindow(native.Handle, 9);
            }
            main.WindowState = WindowState.Normal;
            main.Show(); main.Activate();
            typeof(MainWindow).GetMethod("ShowReverseControlStatus", Instance)!
                .Invoke(main, [Enum.Parse(ModeType, "Usb"), "native-owner-fixture-missing-device", (nint)0]);
            status = app.Windows.OfType<ReverseControlStatusWindow>().Single();
            Require(ReferenceEquals(status.Owner, main), "An explicit main-window start must replace the previous native route.");
            using var replacement = new HwndSource(new HwndSourceParameters("Replacement independent owner fixture")
            { WindowStyle = 0x10CF0000, Width = 240, Height = 340, PositionX = 1080, PositionY = 120 });
            typeof(MainWindow).GetMethod("OnIndependentUsbControlRequested", Instance)!
                .Invoke(main, ["native-owner-fixture-missing-device", replacement.Handle]);
            status = app.Windows.OfType<ReverseControlStatusWindow>().Single();
            Require(GetWindow(new WindowInteropHelper(status).Handle, 4) == replacement.Handle,
                "Another explicit independent start must rebind the native owner.");
            native.Dispose();
            Pump(TimeSpan.FromMilliseconds(80));
            Require(status.IsVisible, "Disposing an old native owner must not close a rerouted status window.");
            replacement.Dispose();
            Pump(TimeSpan.FromMilliseconds(80));
            Require(!status.IsVisible, "Destroying the native owner must close its control status window.");
            status = Show(main, service);
            Require(ReferenceEquals(status.Owner, main), "A disposed native owner must release the route before the next Show.");
            Console.WriteLine("PASS: real independent menu owner, VM reuse, hidden/minimized main, native close focus and owner disposal.");
        }
        finally
        {
            if (status?.IsVisible == true) status.Close();
            Program.CloseWorkspaceTestWindow(main);
            app.MainWindow = previousMain;
            originalOwner.Activate();
        }
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);

    private static void ActivateForeground(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        window.Activate(); SetForegroundWindow(handle);
        if (GetForegroundWindow() != handle)
        {
            // Match the native-focus fixtures: the unattended launcher may not
            // own the system foreground queue. Attach only for this explicit
            // fixture activation, then detach before exercising production close.
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, true);
            try { window.Activate(); SetForegroundWindow(handle); }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        }
        Pump(TimeSpan.FromMilliseconds(80));
        Require(window.IsActive && GetForegroundWindow() == handle,
            $"Foreground fixture activation failed: expected={handle}, actual={GetForegroundWindow()}.");
    }

    private static void TestQueuedSuccess(Window owner, string modeName, bool workerReports = false)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        var mode = Enum.Parse(ModeType, modeName);
        Call(service, "Begin", mode, "Runtime test iPhone");
        var callbackCount = 0;
        var window = Show(owner, service, () => callbackCount++);
        var closed = false;
        var closedOnDispatcher = false;
        var elapsed = Stopwatch.StartNew();
        var closedAt = TimeSpan.Zero;
        window.Closed += (_, _) =>
        {
            closed = true;
            closedOnDispatcher = window.Dispatcher.CheckAccess();
            closedAt = elapsed.Elapsed;
        };
        var labels = new List<string>();
        ((INotifyPropertyChanged)window.DataContext).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "CountdownText")
                labels.Add((string)window.DataContext.GetType().GetProperty("CountdownText")!
                    .GetValue(window.DataContext)!);
        };
        try
        {
            // Matches EnableUsbControlCoreAsync: these reports occur in one
            // continuation, before the dispatcher's queued Apply calls run.
            void Connect()
            {
                Report(service, mode, "Connecting");
                Report(service, mode, "InitializingServices");
                Report(service, mode, "StartingInputRouter");
                Call(service, "Ready", mode, "Runtime test iPhone", "Connected");
            }
            if (workerReports) Task.Run(Connect).GetAwaiter().GetResult();
            else Connect();
            Pump(TimeSpan.FromSeconds(2));
            Require(window.IsVisible && !closed, "Success must remain visible during its countdown.");
            // Duplicate ready notifications must not restart the countdown or
            // replace the displayed instance.
            Call(service, "Ready", mode, "Runtime test iPhone", "Still connected");
            Require(ReferenceEquals(window, Show(owner, service)), "Show must reuse the visible instance.");
            Pump(TimeSpan.FromSeconds(4));
            Console.WriteLine($"{modeName} (worker={workerReports}): closed_at={closedAt.TotalSeconds:F2}s, " +
                $"closed={closed}, visible={window.IsVisible}, callback={callbackCount}, " +
                $"labels={string.Join(" | ", labels.Distinct())}");
            if (!closed)
            {
                var model = window.DataContext;
                Console.WriteLine($"Unclosed countdown: remaining={model.GetType().GetField("_remaining", Instance)!.GetValue(model)}, " +
                    $"armed={model.GetType().GetField("_countdownArmed", Instance)!.GetValue(model)}, " +
                    $"elapsed={model.GetType().GetField("_countdownElapsed", Instance)!.GetValue(model)}, " +
                    $"display_timer_enabled={((DispatcherTimer)model.GetType().GetField("_timer", Instance)!.GetValue(model)!).IsEnabled}");
            }
            Require(closed && closedOnDispatcher && !window.IsVisible,
                $"{modeName}: the visible status window must reach Closed after the five-second countdown.");
            Require(callbackCount == 1, "The completion callback must run exactly once.");
            Require(closedAt.TotalSeconds >= 4.95 && closedAt.TotalSeconds < 6,
                "Repeated ready reports must preserve the original five-second deadline.");
            Require(labels.Contains(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                (string)Application.Current.FindResource("ControlCloseCountdown"), 1)),
                "The visible countdown must advance to its final second.");
            Require(!Application.Current.Windows.Cast<Window>().Contains(window),
                "Close must remove the actual displayed instance from Application.Windows.");
        }
        finally { if (!closed) window.Close(); }
    }

    private static void TestDisplayTimerCompletesCountdown(Window owner)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        var mode = Enum.Parse(ModeType, "Usb");
        Call(service, "Begin", mode, "Runtime test iPhone");
        var completed = 0;
        var window = Show(owner, service, () => completed++);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            Call(service, "Ready", mode, "Runtime test iPhone", "Connected");
            Pump(TimeSpan.FromMilliseconds(100));
            // Reproduce a missing one-shot completion (for example, its
            // callback arrived just before the absolute deadline). Reaching
            // zero in the display timer must still close the real window.
            var timer = (System.Threading.Timer)window.DataContext.GetType()
                .GetField("_deadlineTimer", Instance)!.GetValue(window.DataContext)!;
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Call(window.DataContext, "OnCountdownDeadline");
            Require(!closed, "An early pool-timer callback must not close the window before five seconds.");
            Pump(TimeSpan.FromSeconds(5.5));
            Require(closed && !window.IsVisible && completed == 1,
                "The display timer reaching zero must complete auto-close without the pool timer.");
            Require(typeof(ReverseControlStatusWindow).GetField("_active",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
                "Closed must clear the popup manager's active instance.");
            Console.WriteLine("PASS: reaching zero closes the real window even without a pool-timer callback.");
        }
        finally { if (!closed) window.Close(); }
    }

    private static void TestFailure(Window owner)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        var mode = Enum.Parse(ModeType, "Usb");
        Call(service, "Begin", mode, "Runtime test iPhone");
        var completed = 0;
        var window = Show(owner, service, () => completed++);
        try
        {
            Call(service, "Ready", mode, "Runtime test iPhone", "Connected");
            Pump(TimeSpan.FromMilliseconds(100));
            Call(service, "Failed", mode, "Runtime test iPhone", "Connection timed out", "Simulated USB error");
            Pump(TimeSpan.FromSeconds(5.5));
            Require(window.IsVisible && completed == 0, "A failure must cancel success auto-close.");
            Require((string)window.DataContext.GetType().GetProperty("StageDescription")!
                .GetValue(window.DataContext)! == "Connection timed out", "The error must remain displayed.");
            Call(window, "OnCloseClick", window, new RoutedEventArgs());
            Require(!window.IsVisible && completed == 0, "Manual error close must not enable input.");
            Console.WriteLine("PASS: failure/timeout remains visible beyond five seconds; manual close works.");
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    private static void TestPromptAndRecovery(Window owner)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        var mode = Enum.Parse(ModeType, "Usb");
        Call(service, "Begin", mode, "Runtime test iPhone");
        var completed = 0;
        var window = Show(owner, service, () => completed++);
        try
        {
            Call(service, "Ready", mode, "Runtime test iPhone", "Connected");
            Pump(TimeSpan.FromMilliseconds(100));
            var prompt = Activator.CreateInstance(Type("ControlPrompt"),
                [Enum.Parse(Type("ControlPromptType"), "UserActionRequired"),
                    "Developer Mode / trust required", "Keep this prompt visible", "Continue", "Cancel", true, null, null])!;
            var promptTask = (Task)Call(service, "RequestPromptAsync", prompt, CancellationToken.None)!;
            Pump(TimeSpan.FromSeconds(5.5));
            Require(window.IsVisible && completed == 0 && !promptTask.IsCompleted,
                "An unresolved user-action prompt must not auto-close even at Ready.");
            Call(window, "OnPromptPrimaryClick", window, new RoutedEventArgs());
            Pump(TimeSpan.FromMilliseconds(100));
            Require(promptTask.IsCompleted, "The prompt's existing primary action must still resolve it.");
            Report(service, mode, "Recovering");
            Pump(TimeSpan.FromMilliseconds(100));
            Call(service, "Ready", mode, "Runtime test iPhone", "Recovered");
            Pump(TimeSpan.FromSeconds(4));
            Require(window.IsVisible && completed == 0, "Recovery needs a fresh full five-second countdown.");
            Pump(TimeSpan.FromSeconds(1.5));
            Require(!window.IsVisible && completed == 1, "Resolved prompt and recovery must rearm auto-close.");
            Console.WriteLine("PASS: action prompt stays open; resolving it and recovery rearm the countdown.");
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    private static void TestManualCloseAndReplacement(Window owner)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        var mode = Enum.Parse(ModeType, "Bluetooth");
        Call(service, "Begin", mode, "Runtime test iPhone");
        var completed = 0;
        var window = Show(owner, service, () => completed++);
        Call(service, "Ready", mode, "Runtime test iPhone", "Connected");
        Pump(TimeSpan.FromMilliseconds(100));
        CancelEventHandler cancelClose = (_, args) => args.Cancel = true;
        window.Closing += cancelClose;
        try
        {
            Call(window, "OnCloseClick", window, new RoutedEventArgs());
            Require(window.IsVisible && completed == 0,
                "Canceling a Bluetooth ready close must keep input gated while the status window is visible.");
        }
        finally { window.Closing -= cancelClose; }
        // Queue a CloseActive from another thread, then close/reopen before it
        // executes. That queued close must keep targeting the old instance.
        Task.Run(() => typeof(ReverseControlStatusWindow)
            .GetMethod("CloseActive", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null)).GetAwaiter().GetResult();
        Call(window, "OnCloseClick", window, new RoutedEventArgs());
        Require(!window.IsVisible && completed == 1, "Manual Bluetooth ready close must release input once.");
        Call(service, "Begin", mode, "Runtime test iPhone");
        var replacement = Show(owner, service);
        try
        {
            Pump(TimeSpan.FromSeconds(5.5));
            Require(replacement.IsVisible && completed == 1,
                "An old countdown/queued CloseActive must not close a replacement or repeat its callback.");
            Report(service, mode, "CheckingBluetooth");
            replacement.Close();
            Pump(TimeSpan.FromMilliseconds(100));
            // Drains Apply after Dispose: no disposed-timer access or reopen.
            Require(!Application.Current.Windows.Cast<Window>().Any(w => w is ReverseControlStatusWindow),
                "Queued updates after manual close must not recreate the window.");
            Console.WriteLine("PASS: Bluetooth manual close, instance replacement and queued updates after disposal.");
        }
        finally { if (replacement.IsVisible) replacement.Close(); }
    }

    private static void TestForegroundCloseReturnsToOwner(Window owner, string modeName)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        Call(service, "Begin", Enum.Parse(ModeType, modeName), "Runtime test iPhone");
        var window = Show(owner, service);
        try
        {
            ActivateForeground(window);
            Call(window, "OnCloseClick", window, new RoutedEventArgs());
            Pump(TimeSpan.FromMilliseconds(80));
            Console.WriteLine($"{modeName} foreground close: owner_state={owner.WindowState}, " +
                $"owner_visible={owner.IsVisible}, owner_active={owner.IsActive}");
            Require(!window.IsVisible && owner.IsVisible && owner.WindowState != WindowState.Minimized &&
                owner.IsActive && GetForegroundWindow() == new WindowInteropHelper(owner).Handle,
                "Closing a foreground control status window must return to the visible owner without minimizing it.");
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    private static void TestBackgroundCountdownPreservesFocus(Window owner)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        var mode = Enum.Parse(ModeType, "Wireless");
        Call(service, "Begin", mode, "Runtime test iPhone");
        var window = Show(owner, service);
        var cover = new Window
        {
            Title = "Status-close focus fixture", Width = 240, Height = 160,
            ShowInTaskbar = false, Style = null,
        };
        try
        {
            Call(service, "Ready", mode, "Runtime test iPhone", "Connected");
            Pump(TimeSpan.FromMilliseconds(80));
            cover.Show();
            ActivateForeground(cover);
            Require(cover.IsActive && !window.IsActive,
                "The background-close fixture must put another window in the foreground.");
            Call(window, "OnCountdownElapsed");
            Pump(TimeSpan.FromMilliseconds(80));
            Require(!window.IsVisible && cover.IsActive && owner.WindowState != WindowState.Minimized,
                "A background status countdown must close without activating or minimizing its owner.");
            Console.WriteLine("PASS: background countdown preserves the foreground window.");
        }
        finally
        {
            if (window.IsVisible) window.Close();
            cover.Close();
            owner.Activate();
        }
    }

    private static void TestClosePreservesUnavailableOwner(Window owner, bool minimized)
    {
        var service = Activator.CreateInstance(ServiceType, nonPublic: true)!;
        Call(service, "Begin", Enum.Parse(ModeType, "Usb"), "Runtime test iPhone");
        var window = Show(owner, service);
        try
        {
            if (minimized) owner.WindowState = WindowState.Minimized;
            else owner.Hide();
            Pump(TimeSpan.FromMilliseconds(80));
            Call(window, "OnCloseClick", window, new RoutedEventArgs());
            Pump(TimeSpan.FromMilliseconds(80));
            Require(!window.IsVisible && (minimized
                    ? owner.WindowState == WindowState.Minimized
                    : !owner.IsVisible),
                "Closing a control status window must preserve an owner's hidden/minimized state.");
            Console.WriteLine($"PASS: status close preserves owner {(minimized ? "minimized" : "hidden")} state.");
        }
        finally
        {
            if (window.IsVisible) window.Close();
            owner.WindowState = WindowState.Normal;
            owner.Show();
            owner.Activate();
        }
    }

    private static Window Show(Window owner, object service, Action? completed = null)
    {
        typeof(ReverseControlStatusWindow).GetMethod("Show", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [owner, service, null, null, completed]);
        var window = Application.Current.Windows.Cast<Window>().Single(w => w is ReverseControlStatusWindow);
        Require(window.IsVisible, "The production status window must be visible before testing.");
        return window;
    }

    private static void Report(object service, object mode, string stage) =>
        Call(service, "Report", mode, Enum.Parse(StageType, stage), "Runtime test iPhone", stage, true, null, 0, 0);

    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Instance)!.Invoke(target, args);

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
