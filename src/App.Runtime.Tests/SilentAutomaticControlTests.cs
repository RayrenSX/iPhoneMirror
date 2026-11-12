using System.Windows;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunSilentAutomaticControlTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var owner = new Wpf.Ui.Controls.FluentWindow { Width = 250, Height = 200, ShowInTaskbar = false };
        owner.Show();
        int Visible() => app.Windows.OfType<ReverseControlStatusWindow>().Count(window => window.IsVisible);
        void CloseStatus()
        {
            foreach (var window in app.Windows.OfType<ReverseControlStatusWindow>().ToArray()) window.Close();
            DrainDispatcher();
        }
        try
        {
            foreach (var mode in Enum.GetValues<ControlStatusMode>())
            {
                var service = new ControlStatusService { QuietAutomaticStartup = true };
                service.Begin(mode, "Automatic control fixture");
                foreach (var stage in ControlStageWorkflow.GetStages(mode).Concat(new[] { ControlStage.Recovering }))
                {
                    service.Report(mode, stage, "Automatic control fixture", "Working");
                    ReverseControlStatusWindow.Show(owner, service);
                    DrainDispatcher();
                    InteractionAssert(Visible() == 0, $"Automatic {mode}/{stage} unexpectedly opened a status window.");
                }
                service.Ready(mode, "Automatic control fixture");
                ReverseControlStatusWindow.Show(owner, service);
                DrainDispatcher();
                InteractionAssert(Visible() == 0, $"Automatic {mode} success opened a countdown window.");
                service.Failed(mode, "Automatic control fixture", "Test failure");
                ReverseControlStatusWindow.Show(owner, service);
                ReverseControlStatusWindow.Show(owner, service);
                DrainDispatcher();
                InteractionAssert(Visible() == 1, $"Automatic {mode} failure was hidden or duplicated.");
                CloseStatus();
                service.Begin(mode, "Manual retry fixture");
                service.QuietAutomaticStartup = false;
                ReverseControlStatusWindow.Show(owner, service);
                DrainDispatcher();
                InteractionAssert(Visible() == 1, $"Manual {mode} retry lost its progress window.");
                CloseStatus();
                service.QuietAutomaticStartup = true;
                service.Begin(mode, "Action required fixture");
                var prompt = service.RequestPromptAsync(new(ControlPromptType.UserActionRequired,
                    "Required action", "Complete device preparation", "Continue"));
                ReverseControlStatusWindow.Show(owner, service);
                DrainDispatcher();
                InteractionAssert(Visible() == 1 && !prompt.IsCompleted,
                    $"Automatic {mode} hid an action required for startup to proceed.");
                service.ResolvePrompt(new(ControlPromptAction.Primary));
                CloseStatus();
            }
            Console.WriteLine("PASS quiet automatic control: Bluetooth/USB/wireless progress and success stay hidden; errors show once; manual retry and required actions remain available.");
            return 0;
        }
        finally { CloseStatus(); owner.Close(); app.Shutdown(); }
    }
}
