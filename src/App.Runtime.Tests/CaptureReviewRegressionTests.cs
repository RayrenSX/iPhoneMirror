using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using IPhoneMirror.App;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestCaptureReviewRegressions()
    {
        foreach (var transition in new[] { "selection", "restart", "background", "shutdown" })
            TestStaleCaptureStatus(transition);
        TestDeferredCaptureRelease();
        TestQueuedCaptureWarning();
        TestBackgroundErrorNoticeDoesNotOwnGate();
        TestSelectedErrorReleasesBeforeNotice();
        TestFailedCaptureWiredTeardown(restoreWarning: false);
        TestFailedCaptureWiredTeardown(restoreWarning: true);
        TestFailedCaptureWiredTeardown(restoreWarning: false, throwingObserver: true);
        Console.WriteLine("Capture review regressions passed: stale status, deferred generation checks, wired cleanup ordering and restore recovery.");
    }

    private static void TestStaleCaptureStatus(string transition)
    {
        using var fixture = new CaptureReviewFixture();
        var (a, oldHandle) = fixture.State("review-A", 101);
        var (b, _) = fixture.State("review-B", 202);
        SetKeyboardField(fixture.Vm, "_selectedDevice", fixture.Device("review-A"));
        if (transition == "background") SetKeyboardField(fixture.Vm, "_selectedDevice", null);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.SetReader(handle =>
        {
            if (ReferenceEquals(handle, oldHandle))
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return fixture.Status(ReferenceEquals(handle, oldHandle) ? "Error" : "Streaming");
        });
        // Do not let a second background state enter an unrelated native/UI path.
        if (transition == "background") KeyboardCall(fixture.Sessions, "Remove", "review-B");
        var poll = transition == "background"
            ? (Task)KeyboardCall(fixture.Vm, "PollBackgroundSessionErrorsAsync", false)!
            : (Task)KeyboardCall(fixture.Vm, "RefreshActiveSessionStatusAsync")!;
        try
        {
            InteractionAssert(entered.Wait(TimeSpan.FromSeconds(5)), "Status reader did not start.");
            if (transition == "selection")
                SetKeyboardField(fixture.Vm, "_selectedDevice", fixture.Device("review-B"));
            else if (transition == "shutdown") SetKeyboardField(fixture.Vm, "_disposed", true);
            else fixture.Property(a, "Handle", fixture.Handle(303));
        }
        finally { release.Set(); }
        WaitReviewTask(poll);
        InteractionAssert(fixture.Stops == 0 &&
            !(bool)fixture.Property(a, "ErrorShown")! && !(bool)fixture.Property(b, "ErrorShown")!,
            $"Stale {transition} status affected a live session.");
        InteractionAssert(fixture.Vm.GetType().GetField("_lastCaptureStatus", KeyboardTestMembers)!
            .GetValue(fixture.Vm) is null, $"Stale {transition} status entered the selected cache.");
    }

    private static void TestDeferredCaptureRelease()
    {
        using var fixture = new CaptureReviewFixture();
        var (state, handle) = fixture.State("review-deferred", 401);
        var gate = (SemaphoreSlim)KeyboardField(fixture.Vm, "_coreGate");
        gate.Wait();
        Task cleanup;
        var replacement = fixture.Handle(402);
        try
        {
            cleanup = (Task)KeyboardCall(fixture.Vm, "ReleaseFailedSessionAsync",
                state, handle, fixture.Status("Error"))!;
            InteractionAssert(!cleanup.IsCompleted, "Failed cleanup did not respect lifecycle serialization.");
            fixture.Property(state, "Handle", replacement);
        }
        finally { gate.Release(); }
        WaitReviewTask(cleanup);
        InteractionAssert(fixture.Stops == 0 && ReferenceEquals(fixture.Property(state, "Handle"), replacement),
            "Deferred failure cleanup destroyed a replacement session.");
    }

    private static void TestQueuedCaptureWarning()
    {
        using var fixture = new CaptureReviewFixture();
        var (state, handle) = fixture.State("review-warning", 451);
        var gate = (SemaphoreSlim)KeyboardField(fixture.Vm, "_coreGate");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sawNotice = false;
        var timedOut = false;
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            foreach (System.Windows.Window window in System.Windows.Application.Current.Windows.Cast<System.Windows.Window>().ToArray())
            {
                if (window.GetType().Name != "CaptureStatusNoticeWindow") continue;
                sawNotice = true;
                if (fixture.Stops == 0 && watch.Elapsed < TimeSpan.FromSeconds(3)) continue;
                timedOut = fixture.Stops == 0;
                window.Close();
            }
        };
        timer.Start();
        gate.Wait();
        try
        {
            KeyboardCall(fixture.Vm, "ShowDeviceSessionClosedWarningThenRelease", state, handle,
                fixture.Status("Error"), "Review capture warning", "Review fixture");
            InteractionAssert(watch.Elapsed < TimeSpan.FromSeconds(1),
                "Modal warning blocked the lifecycle-gate owner.");
        }
        finally { gate.Release(); }
        try
        {
            while ((!sawNotice || fixture.Destroys == 0) && watch.Elapsed < TimeSpan.FromSeconds(5))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            InteractionAssert(sawNotice && !timedOut && fixture.Stops == 1 && fixture.Destroys == 1,
                "Visible capture warning delayed native teardown until the dialog closed.");
        }
        finally { timer.Stop(); }
    }

    private static void TestBackgroundErrorNoticeDoesNotOwnGate()
    {
        using var fixture = new CaptureReviewFixture();
        fixture.State("review-background-error", 461);
        fixture.SetReader(_ => fixture.Status("Error"));
        var gate = (SemaphoreSlim)KeyboardField(fixture.Vm, "_coreGate");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sawNotice = false;
        var timedOut = false;
        Task? poll = null;
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            foreach (System.Windows.Window window in System.Windows.Application.Current.Windows.Cast<System.Windows.Window>().ToArray())
            {
                if (window.GetType().Name != "CaptureStatusNoticeWindow") continue;
                sawNotice = true;
                if (poll?.IsCompleted != true && watch.Elapsed < TimeSpan.FromSeconds(3)) continue;
                timedOut = poll?.IsCompleted != true;
                window.Close();
            }
        };
        timer.Start();
        gate.Wait();
        try
        {
            poll = (Task)KeyboardCall(fixture.Vm, "PollBackgroundSessionErrorsAsync", true)!;
            WaitReviewTask(poll);
        }
        finally { gate.Release(); }
        try
        {
            while (!sawNotice && watch.Elapsed < TimeSpan.FromSeconds(5))
                AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            InteractionAssert(sawNotice && !timedOut && fixture.Stops == 1 && fixture.Destroys == 1,
                "Background error notice retained its caller's lifecycle gate until manual close.");
        }
        finally { timer.Stop(); }
    }

    private static void TestSelectedErrorReleasesBeforeNotice()
    {
        using var fixture = new CaptureReviewFixture();
        var (state, handle) = fixture.State("review-selected-error", 471);
        SetKeyboardField(fixture.Vm, "_selectedDevice", fixture.Device("review-selected-error"));
        var sawNotice = false;
        var releasedBeforeNotice = false;
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            foreach (System.Windows.Window window in System.Windows.Application.Current.Windows.Cast<System.Windows.Window>().ToArray())
            {
                if (window.GetType().Name != "CaptureStatusNoticeWindow") continue;
                sawNotice = true;
                releasedBeforeNotice = fixture.Stops == 1 && fixture.Destroys == 1 &&
                    fixture.Property(state, "Handle") is null;
                window.Close();
            }
        };
        timer.Start();
        try
        {
            var cleanup = (Task)KeyboardCall(fixture.Vm, "ReleaseSelectedFailedSessionAsync",
                state, handle, fixture.Status("Error"), "Review selected error", "Review fixture")!;
            WaitReviewTask(cleanup);
            InteractionAssert(sawNotice && releasedBeforeNotice,
                "Selected capture displayed its error before releasing the failed session.");
        }
        finally { timer.Stop(); }
    }

    private static void TestFailedCaptureWiredTeardown(bool restoreWarning, bool throwingObserver = false)
    {
        using var fixture = new CaptureReviewFixture();
        const string physical = "review-failed-physical";
        var (state, handle) = fixture.State(physical, 501);
        var control = KeyboardCall(fixture.Vm, "GetOrCreateControl", "airplay://failed-alias")!;
        SetKeyboardField(control, "AppleUdid", physical);
        SetKeyboardField(control, "WiredEnabled", true);
        SetKeyboardField(control, "Starting", true);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var cleaned = false;
        Func<CancellationToken, Task> operation = async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled = true; await finished.Task; cleaned = true; }
        };
        KeyboardCall(KeyboardField(control, "WiredOperation"), "RunAsync", operation, CancellationToken.None);
        fixture.OnStop = _ =>
        {
            InteractionAssert(cleaned && !(bool)KeyboardField(control, "WiredEnabled"),
                "Native teardown ran before physical wired-control cleanup.");
            if (restoreWarning)
                throw (Exception)Activator.CreateInstance(fixture.Type("Interop.UsbConfigurationRestoreWarningException"),
                    KeyboardTestMembers, null, ["review restore warning", -12], null)!;
        };
        var observerThrew = false;
        if (throwingObserver)
            ((System.ComponentModel.INotifyPropertyChanged)fixture.Vm).PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != "CurrentSessionHandle" || observerThrew) return;
                observerThrew = true;
                throw new InvalidOperationException("review UI observer failure");
            };
        var cleanup = (Task)KeyboardCall(fixture.Vm, "ReleaseFailedSessionAsync", state, handle, fixture.Status("Error"))!;
        try
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            InteractionAssert(cancelled && fixture.Stops == 0 && !cleanup.IsCompleted,
                "Failed capture must await wired startup cancellation before native stop.");
            InteractionAssert((bool)fixture.Property(state, "IsStopping")!, "Failed preview remained presentable during cleanup.");
        }
        finally { finished.TrySetResult(); }
        WaitReviewTask(cleanup);
        InteractionAssert(fixture.Stops == 1 && fixture.Destroys == 1 &&
            fixture.Property(state, "Handle") is null && !(bool)fixture.Property(state, "IsStopping")!,
            "Failure teardown did not fully release its session.");
        InteractionAssert((bool)KeyboardCall(KeyboardField(fixture.Vm, "_usbRestoreRecovery"), "IsBlocked", physical)! == restoreWarning,
            "Unconfirmed USB restore did not enter recovery protection.");
        InteractionAssert(observerThrew == throwingObserver, "The throwing UI observer was not exercised.");
    }

    private sealed class CaptureReviewFixture : IDisposable
    {
        internal readonly object Vm;
        internal readonly object Sessions;
        private readonly object _originalSessions;
        private readonly List<IDisposable> _handles = [];
        internal int Stops;
        internal int Destroys;
        internal Action<object>? OnStop;
        internal Type Type(string name) => typeof(App).Assembly.GetType("IPhoneMirror.App." + name, true)!;

        internal CaptureReviewFixture()
        {
            Vm = Activator.CreateInstance(Type("ViewModels.MainViewModel"))!;
            _originalSessions = KeyboardField(Vm, "_sessions");
            var callbackType = typeof(Action<>).MakeGenericType(Type("Interop.NativeSessionHandle"));
            Action<object> stop = handle => { Interlocked.Increment(ref Stops); OnStop?.Invoke(handle); };
            Action<object> destroy = _ => Interlocked.Increment(ref Destroys);
            Sessions = Activator.CreateInstance(_originalSessions.GetType(), KeyboardTestMembers, null,
                [Delegate.CreateDelegate(callbackType, stop.Target, stop.Method),
                 Delegate.CreateDelegate(callbackType, destroy.Target, destroy.Method)], null)!;
            SetKeyboardField(Vm, "_sessions", Sessions);
        }

        internal object Handle(ulong raw)
        {
            var handle = (IDisposable)Activator.CreateInstance(Type("Interop.NativeSessionHandle"),
                KeyboardTestMembers, null, [raw, false], null)!;
            _handles.Add(handle);
            return handle;
        }
        internal (object, object) State(string udid, ulong raw)
        {
            var state = Activator.CreateInstance(Type("Models.DeviceCaptureState"))!;
            Property(state, "Udid", udid);
            var handle = Handle(raw);
            Property(state, "Handle", handle);
            KeyboardCall(Sessions, "Set", state);
            return (state, handle);
        }
        internal object Device(string udid)
        {
            var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
            return ctor.Invoke([udid, udid, "iPhone15,2", "18.0", "USB", "",
                Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
        }
        internal object? Property(object target, string name) =>
            target.GetType().GetProperty(name, KeyboardTestMembers)!.GetValue(target);
        internal void Property(object target, string name, object value) =>
            target.GetType().GetProperty(name, KeyboardTestMembers)!.SetValue(target, value);
        internal object Status(string name)
        {
            var status = Activator.CreateInstance(Type("Interop.NativeCaptureStatus"))!;
            SetKeyboardField(status, "State", Enum.Parse(Type("Interop.CaptureState"), name));
            SetKeyboardField(status, "Message", "review status");
            return status;
        }
        internal void SetReader(Func<object, object> reader)
        {
            var field = Vm.GetType().GetField("_captureStatusReader", KeyboardTestMembers)!;
            var signature = field.FieldType.GetGenericArguments();
            var parameter = Expression.Parameter(signature[0]);
            var body = Expression.Convert(Expression.Invoke(Expression.Constant(reader),
                Expression.Convert(parameter, typeof(object))), signature[1]);
            field.SetValue(Vm, Expression.Lambda(field.FieldType, body, parameter).Compile());
        }
        public void Dispose()
        {
            ((IDictionary)KeyboardField(Vm, "_deviceControls")).Clear();
            SetKeyboardField(Vm, "_selectedDevice", null);
            SetKeyboardField(Vm, "_sessions", _originalSessions);
            SetKeyboardField(Vm, "_disposed", false);
            WaitReviewTask((Task)KeyboardCall(Vm, "ShutdownAsync")!);
            foreach (var handle in _handles) handle.Dispose();
        }
    }
}
