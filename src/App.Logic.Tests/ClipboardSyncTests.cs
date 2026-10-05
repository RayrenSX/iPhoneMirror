using System.Runtime.InteropServices;
using IPhoneMirror.App.Services;

internal static class ClipboardSyncTests
{
    internal static async Task RunAsync()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var source = new object();
        var writes = new List<string>();
        var attempts = 0;
        var completions = 0;
        uint windowsSequence = 1;
        var sync = new ClipboardSyncState(text =>
        {
            if (++attempts < 3) throw new ExternalException("clipboard busy");
            writes.Add(text);
            windowsSequence++;
        }, () => windowsSequence, (_, candidate) => ReferenceEquals(candidate, source),
            (_, _, error) => { Check(error is null, "Unexpected clipboard failure."); completions++; },
            () => Task.CompletedTask);
        void Observe(string device, object origin, string? text) =>
            sync.Observe(device, origin, text, sync.CaptureSequence());
        sync.SelectDevice("A");
        sync.SelectDevice(null);
        Observe("A", source, "中文 / café / 😀");
        await sync.FlushAsync();
        await sync.FlushAsync();
        Check(attempts == 3 && completions == 1 && writes.Count == 1,
            "One bridge event must retry busy clipboard writes and sync while the window is inactive.");

        Observe("B", source, "second device");
        await sync.FlushAsync();
        Check(writes.Count == 1, "An unselected phone overwrote the Windows clipboard.");
        sync.SelectDevice("b");
        await sync.FlushAsync();
        Check(writes.Last() == "second device", "Selecting a cached device must apply its pending update.");
        Observe("B", source, "");
        await sync.FlushAsync();
        Observe("B", source, "second device");
        await sync.FlushAsync();
        Check(writes.Count == 3, "A -> empty -> A must allow the same text to be copied again.");

        // Both snapshots start at the same Windows sequence. Synchronizing A
        // advances it, but must not invalidate B; only an external copy can.
        writes.Clear();
        sync = new ClipboardSyncState(text => { writes.Add(text); windowsSequence++; },
            () => windowsSequence, (_, _) => true, (_, _, _) => { });
        sync.SelectDevice("A");
        Observe("B", source, "cached-B");
        Observe("A", source, "current-A");
        await sync.FlushAsync();
        sync.SelectDevice("B");
        await sync.FlushAsync();
        Check(writes.SequenceEqual(["current-A", "cached-B"]),
            "Our Windows clipboard write incorrectly expired another phone's cache.");
        Observe("A", source, "older-A");
        windowsSequence++; // A real copy from another Windows application.
        Observe("C", source, "newer-C");
        sync.SelectDevice("C");
        await sync.FlushAsync();
        sync.SelectDevice("A");
        await sync.FlushAsync();
        Check(writes.SequenceEqual(["current-A", "cached-B", "newer-C"]),
            "Our write revived a cache that was already invalidated by an external copy.");

        // Arrival occurs on a bridge reader, before its dispatcher callback.
        writes.Clear();
        sync.SelectDevice("A");
        var queued = await Task.Run(sync.CaptureSequence);
        windowsSequence++; // User copies while the event is still queued.
        sync.Observe("A", source, "queued-before-local-copy", queued);
        await sync.FlushAsync();
        Check(writes.Count == 0, "A queued event overwrote a newer local copy.");

        // Unlike an external copy, our own writes must carry queued events
        // forward even before those events have entered the device cache.
        queued = await Task.Run(sync.CaptureSequence);
        Observe("A", source, "own-write-one");
        await sync.FlushAsync();
        Observe("A", source, "own-write-two");
        await sync.FlushAsync();
        sync.Observe("B", source, "queued-across-own-writes", queued);
        sync.SelectDevice("B");
        await sync.FlushAsync();
        Check(writes.SequenceEqual(["own-write-one", "own-write-two", "queued-across-own-writes"]),
            "Our writes invalidated an event still in the dispatcher queue.");

        queued = await Task.Run(sync.CaptureSequence);
        windowsSequence++;
        Observe("B", source, "own-write-after-local-copy");
        await sync.FlushAsync();
        sync.Observe("A", source, "queued-before-local-and-own-writes", queued);
        sync.SelectDevice("A");
        await sync.FlushAsync();
        Check(writes.Count == 4 && writes.Last() == "own-write-after-local-copy",
            "Our write revived a queued event invalidated by an external copy.");

        // Capture must not wait for a potentially blocking OLE write. Capture
        // on both sides of its sequence change, while the writer is still in
        // progress, then carry both tokens across another successful write.
        ClipboardSyncState.SequenceSnapshot? duringWriteBefore = null;
        ClipboardSyncState.SequenceSnapshot? duringWriteAfter = null;
        var captureDuringWrite = true;
        writes.Clear();
        sync = new ClipboardSyncState(text =>
        {
            if (captureDuringWrite)
            {
                duringWriteBefore = Task.Run(sync.CaptureSequence).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                windowsSequence++;
                duringWriteAfter = Task.Run(sync.CaptureSequence).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                captureDuringWrite = false;
            }
            else windowsSequence++;
            writes.Add(text);
        }, () => windowsSequence, (_, _) => true, (_, _, error) =>
            Check(error is null, "Arrival capture was blocked by our clipboard write."));
        sync.SelectDevice("A");
        Observe("A", source, "first-own-write");
        await sync.FlushAsync();
        Observe("A", source, "second-own-write");
        await sync.FlushAsync();
        sync.Observe("B", source, "captured-before", duringWriteBefore!);
        sync.SelectDevice("B");
        await sync.FlushAsync();
        sync.Observe("C", source, "captured-after", duringWriteAfter!);
        sync.SelectDevice("C");
        await sync.FlushAsync();
        Check(writes.SequenceEqual(["first-own-write", "second-own-write", "captured-before", "captured-after"]),
            "An event captured during our write was blocked or incorrectly invalidated.");

        foreach (var scenario in new[] { "new-device-text", "new-windows-text", "switch-device", "empty", "replaced-bridge", "stop" })
        {
            var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            uint sequence = 10;
            var sourceCurrent = true;
            attempts = 0;
            writes.Clear();
            sync = new ClipboardSyncState(text =>
            {
                if (++attempts == 1) throw new ExternalException("busy");
                writes.Add(text);
                sequence++;
            }, () => sequence, (_, _) => sourceCurrent, (_, _, error) =>
                Check(error is null, "Retry unexpectedly failed."), async () =>
                {
                    delayed.SetResult();
                    await release.Task;
                });
            sync.SelectDevice("A");
            Observe("A", source, "stale");
            var pump = sync.FlushAsync();
            await delayed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            switch (scenario)
            {
                case "new-device-text": Observe("A", source, "new"); break;
                case "new-windows-text": sequence++; break;
                case "switch-device":
                    Observe("B", source, "new");
                    sync.SelectDevice("B");
                    break;
                case "empty": Observe("A", source, null); break;
                case "replaced-bridge": sourceCurrent = false; break;
                case "stop": sync.Stop(); break;
            }
            release.SetResult();
            await pump.WaitAsync(TimeSpan.FromSeconds(3));
            var expected = scenario is "new-device-text" or "switch-device" ? new[] { "new" } : [];
            Check(writes.SequenceEqual(expected), $"Stale clipboard write after {scenario}.");
        }

        // A clipboard held longer than one retry burst must still receive the
        // same device copy when it becomes writable; unchanged phone text is
        // not published again by the bridge.
        attempts = completions = 0;
        var busyReports = 0;
        writes.Clear();
        sync = new ClipboardSyncState(text =>
        {
            if (++attempts <= 6) throw new ExternalException("still busy");
            writes.Add(text);
            windowsSequence++;
        }, () => windowsSequence, (_, _) => true,
            (_, _, error) =>
            {
                if (error is null) completions++;
                else busyReports++;
            }, () => Task.CompletedTask);
        sync.SelectDevice("A");
        Observe("A", source, "eventually writable");
        await sync.FlushAsync();
        Check(attempts == 7 && busyReports == 1 && completions == 1 &&
              writes.SequenceEqual(["eventually writable"]),
            "A long clipboard lock permanently lost the device copy.");

        attempts = completions = 0;
        sync = new ClipboardSyncState(_ => { attempts++; throw new ExternalException("still busy"); },
            () => 1, (_, _) => true, (_, _, error) =>
            {
                if (error is null) return;
                completions++;
                sync.Stop();
            }, () => Task.CompletedTask);
        sync.SelectDevice("A");
        Observe("A", source, "retry limit");
        await sync.FlushAsync();
        Check(attempts == 5 && completions == 1,
            "A busy clipboard must report one retry burst and stop after shutdown.");
        Console.WriteLine("Clipboard sync: background routing, cache, retries, stale writes and shutdown passed.");
    }
}
