using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunControlErrorPresentationTests()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        var main = new MainWindow();
        app.MainWindow = null;
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        try
        {
            vm.ControlStatus.Begin(ControlStatusMode.Wireless, "Test phone");
            KeyboardCall(vm, "ShowReverseControlError", "wireless", "Original wireless failure",
                "ReverseControlStartErrorTitle", "wireless_remote_pairing_failed: original diagnostic", null, null);
            MappingAssert(vm.ControlStatus.Current is { Stage: ControlStage.Failed, Error: "Original wireless failure" },
                "Missing window lost the original control failure.");
            MappingAssert(vm.ControlStatus.Diagnostics.Any(d => d.TechnicalMessage.Contains("wireless_remote_pairing_failed")),
                "Missing window discarded the diagnostic.");
            MappingAssert((int)KeyboardField(vm, "_reverseControlErrorPromptInFlight") == 0,
                "Missing window left the error prompt guard set.");
            Console.WriteLine("Control failure without a main window retains status/diagnostics and releases the prompt guard.");
            return 0;
        }
        finally
        {
            AwaitMapping(vm.ShutdownAsync());
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }
}
