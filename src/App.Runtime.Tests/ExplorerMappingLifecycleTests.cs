using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Services.ExplorerMapping;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunExplorerMappingLifecycleTests(string output)
    {
        Console.WriteLine("STAGE mapping lifecycle: initializing isolated application.");
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        Console.WriteLine("STAGE mapping lifecycle: creating isolated workspace.");
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        Console.WriteLine("STAGE mapping lifecycle: creating isolated devices.");
        var vm = (MainViewModel)main.DataContext;
        vm.Devices.CollectionChanged -= (System.Collections.Specialized.NotifyCollectionChangedEventHandler)
            Delegate.CreateDelegate(typeof(System.Collections.Specialized.NotifyCollectionChangedEventHandler), main,
                typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
        var a = DeviceViewModel.FromNative(new NativeDeviceInfo { Udid = "explorer-lifecycle-a", Name = "Fixture A", State = ConnectionState.Ready }, DeviceAppearance.Unknown);
        var b = DeviceViewModel.FromNative(new NativeDeviceInfo { Udid = "explorer-lifecycle-b", Name = "Fixture B", State = ConnectionState.Ready }, DeviceAppearance.Unknown);
        vm.Devices.Add(a); vm.Devices.Add(b); KeyboardCall(vm, "SetSelectedDevice", a, false);
        Console.WriteLine("STAGE mapping lifecycle: testing pending startup cancellation.");
        try
        {
            var oldCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetKeyboardField(vm, "_explorerCleanup", oldCleanup.Task);
            var oldStart = vm.SetExplorerMappingAsync(a, true);
            var oldSession = KeyboardField(vm, "_explorerMapping");
            InteractionAssert(vm.ExplorerMappingBusy && vm.ExplorerMappingDeviceUdid == a.Udid && !oldStart.IsCompleted,
                "A startup waiting for previous cleanup must already publish its cancellable identity.");
            KeyboardCall(vm, "SetSelectedDevice", b, false);
            InteractionAssert(SessionToken(oldSession).IsCancellationRequested && vm.ExplorerMappingDeviceUdid is null && !vm.ExplorerMappingBusy,
                "Changing selection must immediately revoke a startup waiting for cleanup.");
            // Keep a newer owner pending without opening a server or WNet. The
            // previous startup's catch/finally must never modify this owner.
            var newer = NewExplorerSession(b);
            SetKeyboardField(vm, "_explorerMapping", newer);
            SetKeyboardField(vm, "_explorerMappingBusy", true);
            SetKeyboardField(vm, "_explorerStatusKey", "DeviceHubExplorerStarting");
            oldCleanup.SetResult();
            Console.WriteLine("STAGE mapping lifecycle: joining cancelled old startup.");
            FinishExplorer(oldStart);
            InteractionAssert(!oldStart.Result && ReferenceEquals(KeyboardField(vm, "_explorerMapping"), newer) &&
                vm.ExplorerMappingBusy && (string)KeyboardField(vm, "_explorerStatusKey") == "DeviceHubExplorerStarting",
                "An old cancelled startup overwrote a newer mapping's owner, busy flag or status.");
            SessionFinished(newer).SetResult();
            FinishExplorer(StopExplorer(vm));
            Console.WriteLine("PASS mapping startup: identity before await, immediate selection cancellation and stale completion isolation.");

            // A server disposal fault must not skip drive cleanup, and a drive
            // timeout/fault must never drop the older cleanup completion chain.
            var session = NewExplorerSession(b);
            var driveDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetKeyboardField(session, "Server", RuntimeHelpers.GetUninitializedObject(typeof(ExplorerWebDavServer)));
            SetKeyboardField(session, "Drive", InertExplorerDrive(driveDisposed.Task));
            SessionFinished(session).SetResult();
            SetKeyboardField(vm, "_explorerMapping", session);
            SetKeyboardField(vm, "_explorerCleanup", previous.Task);
            var cleanup = StopExplorer(vm);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(!cleanup.IsCompleted, "A server cleanup fault skipped the pending drive cleanup.");
            driveDisposed.SetException(new IOException("INJECTED_DISCONNECT_FAILURE"));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(!cleanup.IsCompleted, "A drive cleanup fault dropped the previous cleanup chain.");
            previous.SetResult(); FinishExplorer(cleanup);
            Console.WriteLine("PASS mapping cleanup: server fault still joins drive, drive fault still joins previous cleanup.");

            // Stop may happen while ConnectAsync is about to return a local
            // drive. Cleanup must join the startup before reading its resources.
            session = NewExplorerSession(b);
            SetKeyboardField(vm, "_explorerMapping", session);
            cleanup = StopExplorer(vm);
            InteractionAssert(SessionToken(session).IsCancellationRequested && !cleanup.IsCompleted,
                "Stop must revoke immediately but join an unfinished startup.");
            driveDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SetKeyboardField(session, "Drive", InertExplorerDrive(driveDisposed.Task));
            SessionFinished(session).SetResult();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            InteractionAssert(!cleanup.IsCompleted, "Stop lost the drive returned by an already-cancelled startup.");
            driveDisposed.SetResult(); FinishExplorer(cleanup);
            Console.WriteLine("PASS mapping late resource: stop waits for startup and its returned drive cleanup.");

            var control = (DeviceControlSession)KeyboardCall(vm, "GetOrCreateControl", b.Udid)!;
            var bindings = new DeviceBindingManager(Path.Combine(output, $"explorer-bindings-{Guid.NewGuid():N}.json"));
            var profile = bindings.CreateProfileFromIdentity(b.Name, DeviceIdentityType.Wired, b.Udid, null).Profile!;
            var resolver = new DeviceIdentityResolver(bindings);
            SetKeyboardField(vm, "_identityResolver", resolver);
            control.Binding = resolver.ResolveControlBinding(b, ReverseControlMode.Usb);
            using var host = new ExplorerReadyBridge(control);
            session = NewExplorerSession(b, host.Host);
            SessionFinished(session).SetResult();
            SetKeyboardField(vm, "_explorerMapping", session);
            InteractionAssert((bool)KeyboardCall(vm, "ExplorerRouteCurrent", session)!, "The pinned ready fixture route must be current.");
            var hubGate = (SemaphoreSlim)KeyboardField(host.Inner, "_hubGate");
            hubGate.Wait();
            try
            {
                var request = (Task<JsonElement>)KeyboardCall(vm, "ExplorerRequestAsync", session, "list_dir",
                    new { root = "media", path = "", offset = 0, limit = 1 }, CancellationToken.None)!;
                try { FinishExplorer(request); throw new InvalidOperationException("A busy manual Device Hub operation admitted an Explorer request."); }
                catch (AutomationException error) when (error.Code == "CONTROL_LOCKED") { }
            }
            finally { hubGate.Release(); }
            SetKeyboardField(host.Inner, "_readyGeneration", 2L);
            InteractionAssert(!(bool)KeyboardCall(vm, "ExplorerRouteCurrent", session)!, "A recovered bridge generation reused the old mapping route.");
            KeyboardCall(vm, "CheckExplorerMappingConnection");
            FinishExplorer((Task)KeyboardField(vm, "_explorerCleanup"));
            InteractionAssert(SessionToken(session).IsCancellationRequested && vm.ExplorerMappingDeviceUdid is null,
                "A bridge generation change did not revoke and clean the mapping.");
            session = NewExplorerSession(b, host.Host);
            SessionFinished(session).SetResult();
            SetKeyboardField(vm, "_explorerMapping", session);
            bindings.Unbind(profile.Id, DeviceIdentityType.Wired);
            InteractionAssert(!(bool)KeyboardCall(vm, "ExplorerRouteCurrent", session)!, "An unbound device must revoke the Explorer route.");
            bindings.Bind(profile.Id, DeviceIdentityType.Wired, b.Udid, b.Name, null, true);
            InteractionAssert((bool)KeyboardCall(vm, "ExplorerRouteCurrent", session)!, "Restoring the same device binding should restore readiness in the fixture.");
            SetKeyboardField(vm, "_disposed", true);
            InteractionAssert(!(bool)KeyboardCall(vm, "ExplorerRouteCurrent", session)!, "Shutdown must invalidate mapping providers before transport cleanup.");
            FinishExplorer(StopExplorer(vm));
            SetKeyboardField(vm, "_disposed", false);
            Console.WriteLine("PASS mapping provider: manual CONTROL_LOCKED, bridge-generation revocation and shutdown invalidation.");
            return 0;
        }
        finally
        {
            Console.WriteLine("STAGE mapping lifecycle: closing isolated workspace.");
            SetKeyboardField(vm, "_disposed", false);
            CloseWorkspaceTestWindow(main); app.Shutdown();
        }
    }

    private static object NewExplorerSession(DeviceViewModel device, UsbTouchBridgeHost? host = null) =>
        typeof(MainViewModel).GetNestedType("ExplorerMappingSession", BindingFlags.NonPublic)!
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single()
            .Invoke([device, host, new CancellationTokenSource()]);
    private static CancellationToken SessionToken(object session) => (CancellationToken)session.GetType()
        .GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    private static TaskCompletionSource SessionFinished(object session) => (TaskCompletionSource)session.GetType()
        .GetProperty("Finished", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    private static WindowsExplorerDrive InertExplorerDrive(Task disposal)
    {
        var drive = (WindowsExplorerDrive)typeof(WindowsExplorerDrive).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single().Invoke(["Z:", @"\\127.0.0.1@1\DavWWWRoot\inert-fixture"]);
        SetKeyboardField(drive, "_disposeTask", disposal);
        return drive;
    }
    private static Task StopExplorer(MainViewModel vm) => (Task)KeyboardCall(vm, "StopExplorerMappingAsync", false, false, false)!;
    private static void FinishExplorer(Task task)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && started.Elapsed < TimeSpan.FromSeconds(5)) AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
        InteractionAssert(task.IsCompleted, "Mapping lifecycle fixture timed out.");
        task.GetAwaiter().GetResult();
    }

    private sealed class ExplorerReadyBridge : IDisposable
    {
        internal UsbTouchBridgeHost Host { get; } = new();
        internal DirectUsbInputBridge Inner { get; }
        private readonly DeviceControlSession _control;
        internal ExplorerReadyBridge(DeviceControlSession control)
        {
            _control = control;
            Inner = (DirectUsbInputBridge)KeyboardField(Host, "_bridge");
            SetKeyboardField(Inner, "<IsReady>k__BackingField", true);
            SetKeyboardField(Inner, "<SupportsDeviceHub>k__BackingField", true);
            SetKeyboardField(Inner, "_readyGeneration", 1L);
            control.WiredBridge = Host;
            control.WiredEnabled = control.WiredConnected = true;
        }
        public void Dispose()
        {
            _control.WiredEnabled = _control.WiredConnected = false; _control.WiredBridge = null;
            SetKeyboardField(Inner, "<IsReady>k__BackingField", false);
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
