using System.Diagnostics;
using System.Windows;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;

namespace IPhoneMirror.DriverInstaller;

public partial class App : Application
{
    private readonly Stopwatch _sessionTimer = Stopwatch.StartNew();
    private bool _elevatedHost;
    private DriverOperationKind? _elevatedKind;
    private string? _elevatedOperation;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
            DriverLogger.WriteException("runtime", "dispatcher_unhandled_exception",
                args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
                DriverLogger.WriteException("runtime", "unhandled_exception", error,
                    ("terminating", args.IsTerminating));
            else
                DriverLogger.WriteError("runtime", "unhandled_non_exception",
                    ("terminating", args.IsTerminating));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DriverLogger.WriteException("runtime", "unobserved_task_exception",
                args.Exception);
            args.SetObserved();
        };
        if (ElevatedDriverHost.IsRequested(e.Args))
        {
            _elevatedHost = true;
            if (e.Args.Length > 1 &&
                Enum.TryParse<DriverOperationKind>(e.Args[1], ignoreCase: false,
                    out var parsedKind))
                _elevatedKind = parsedKind;
            _elevatedOperation = e.Args.Length > 4 ? e.Args[4] : null;
            DriverLogger.WriteEvent("lifecycle", "elevated_session_start",
                ("kind", _elevatedKind?.ToString() ?? "unknown"),
                ("operation", _elevatedOperation ?? "unknown"));
            var exitCode = ElevatedDriverHost.Run(e.Args);
            DriverLogger.WriteEvent("lifecycle", "elevated_operation_returned",
                ("exit_code", exitCode));
            Shutdown(exitCode);
            return;
        }

        base.OnStartup(e);
        DriverLocalization.Initialize(e.Args);
        Resources.MergedDictionaries.Insert(0, DriverLocalization.CreateDictionary());
        DriverLogger.WriteEvent("lifecycle", "ui_session_start",
            ("kind", "interactive"),
            ("culture", DriverLocalization.Culture.Name));
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            DriverLogger.WriteEvent("lifecycle", "session_end",
                ("exit_code", e.ApplicationExitCode),
                ("elapsed_ms", _sessionTimer.ElapsedMilliseconds),
                ("mode", _elevatedHost ? "elevated" : "interactive"),
                ("kind", _elevatedKind?.ToString() ?? "unknown"),
                ("operation", _elevatedOperation ?? "unknown"));
        }
        catch
        {
            // Diagnostics must never prevent application shutdown.
        }
        base.OnExit(e);
    }
}
