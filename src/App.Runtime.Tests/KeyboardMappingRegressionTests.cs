using System.IO;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunKeyboardMappingRegressions()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var app = new IPhoneMirror.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        try
        {
            AwaitMapping(TestMappingFrameCancellationAsync());
            TestMappingPickPreservesDuration();
            AwaitMapping(TestPasteFrameLimitsAsync());
            Console.WriteLine("PASS paste frames remain valid at the IPC size limit.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private sealed class CancelDuringFrameStream(CancellationTokenSource release) : MemoryStream
    {
        private bool _first = true;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Write(bytes.Span);
            if (_first)
            {
                _first = false;
                release.Cancel();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PartialFrameFailureStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            Write(bytes.Span[..2]);
            throw new IOException("Synthetic short pipe write");
        }
    }

    private static async Task TestMappingFrameCancellationAsync()
    {
        using var release = new CancellationTokenSource();
        using var stream = new CancelDuringFrameStream(release);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        var bridge = new DirectUsbInputBridge();
        SetKeyboardField(bridge, "_stdin", writer);
        SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        var route = new MappedTouchRoute("frame", () => true,
            (action, x, y, token) => bridge.SendTouchBatchAsync([new TouchPoint(2, action, x, y)], 1, 1, token),
            (x, y) => (x, y));
        var mapping = MappingEntry(MappedTouchAction.HoldUntilRelease);
        using var executor = new KeyboardMappingExecutor();
        try { await executor.ExecuteAsync(mapping, route, release.Token); }
        catch (OperationCanceledException) { }
        var frames = MappingFrames(stream);
        MappingAssert(frames.Count == 2 && frames[0].GetProperty("points")[0].GetProperty("action").GetString() == "down" &&
            frames[1].GetProperty("points")[0].GetProperty("action").GetString() == "up" && bridge.IsReady,
            "Cancellation during a frame corrupted touch IPC or lost the cleanup release.");
        using var failedStream = new PartialFrameFailureStream();
        using var failedWriter = new StreamWriter(failedStream, leaveOpen: true);
        var failedBridge = new DirectUsbInputBridge();
        SetKeyboardField(failedBridge, "_stdin", failedWriter);
        SetKeyboardField(failedBridge, "<IsReady>k__BackingField", true);
        try
        {
            await failedBridge.SendTouchBatchAsync([new TouchPoint(3, "down", .5, .5)], 1, 1);
            throw new InvalidOperationException("A partial frame write appeared to succeed.");
        }
        catch (IOException) { }
        MappingAssert(!failedBridge.IsReady && failedStream.Length == 2,
            "A partial frame did not retire the damaged IPC stream.");
        try
        {
            await failedBridge.SendTouchBatchAsync([new TouchPoint(3, "up", .5, .5)], 1, 2);
            throw new InvalidOperationException("Input followed a partial frame.");
        }
        catch (InvalidOperationException error) when (error.Message != "Input followed a partial frame.") { }
        Console.WriteLine("PASS touch frame stays complete when held key releases during IPC write.");
    }

    private static void TestMappingPickPreservesDuration()
    {
        var entry = MappingEntry(MappedTouchAction.LongPress) with { DurationMs = 400 };
        KeyboardMappingEntry? saved = null;
        var editor = new KeyboardMappingEditorWindow(entry, [], _ => null, _ => null, () => { },
            (value, _) => { saved = value; return null; },
            (value, complete) => { complete(value with { X = .2, Y = .3 }); return null; });
        try
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(20)); // initialize the wizard step bindings
            var duration = (TextBox)editor.FindName("DurationBox");
            duration.Text = "1200";
            while (editor.Wizard.Current != MappingWizardStep.Position) editor.Wizard.MoveNext();
            KeyboardCall(editor, "OnPickClick", editor, new RoutedEventArgs());
            MappingAssert(duration.Text == "1200" && editor.State == MappingEditorState.MappingReady,
                "Picking a long-press point replaced the user's duration.");
            while (editor.Wizard.Current != MappingWizardStep.Confirmation)
                MappingAssert(editor.Wizard.MoveNext(), "The mapping wizard could not advance to its save confirmation.");
            editor.Wizard.MoveNext();
            KeyboardCall(editor, "OnSaveClick", editor, new RoutedEventArgs());
            MappingAssert(saved is { DurationMs: 1200, X: .2, Y: .3 },
                $"The duration visible after picking was not saved: draft={editor.Wizard.Duration}, saved={saved?.DurationMs}, point={saved?.X},{saved?.Y}.");
        }
        finally { DiscardWizard(editor); }
        Console.WriteLine("PASS long-press duration survives picking a new target point.");
    }
}
