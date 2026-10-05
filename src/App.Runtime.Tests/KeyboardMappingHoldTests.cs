using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static async Task TestMappingHoldUntilReleaseAsync()
    {
        using var executor = new KeyboardMappingExecutor();
        var holds = new KeyboardMappingHoldState();
        var entry = MappingEntry(MappedTouchAction.HoldUntilRelease) with
            { X = .2, Y = .7, EndX = double.NaN, DurationMs = 0 };
        MappingAssert(entry.Validate() is null && !entry.IsSwipe && !entry.IsDirectional,
            "Hold requires an irrelevant duration or swipe endpoint.");
        var events = new List<(string Action, double X, double Y)>();
        var current = true;
        var route = new MappedTouchRoute("hold", () => current, (action, x, y, _) =>
        { events.Add((action, x, y)); return Task.CompletedTask; }, (x, y) => (1 - x, y));
        async Task Cancelled(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Hold did not cancel."); }
            catch (OperationCanceledException) { }
        }

        // Ordinary, modifier and Win bindings must start on down, ignore repeat,
        // release even while execution is disallowed, and never fire again on up.
        foreach (var key in new[] { MappingTestKey, new MappedKey(0xA2, 0x1D, false), new MappedKey(0x5B, 0x5B, true) })
        {
            events.Clear();
            var mapping = entry with { Key = key };
            var keys = new KeyboardMappingKeyState();
            var windows = new KeyboardMappingWindowsKey();
            MappingKeyResult Process(bool down, bool allowed = true) => key.IsWindows
                ? windows.Process(key, down, allowed, false, [mapping], _ => true)
                : keys.Process(key, down, false, allowed, false, true, [mapping]);
            MappingAssert(Process(true).Mapping == mapping, "Hold did not start on key down.");
            var release = holds.Begin(mapping);
            var task = executor.ExecuteAsync(mapping, route, release.Token);
            MappingAssert(Process(true).Mapping is null, "Autorepeat restarted hold.");
            await Task.Delay(130);
            MappingAssert(!task.IsCompleted && events.SequenceEqual(new[] { ("down", .8, .7) }),
                "Held contact moved, repeated or released before key up.");
            holds.Process(key, false);
            MappingAssert(Process(false, false).Mapping is null, "Key up started another hold.");
            await Cancelled(task);
            MappingAssert(events.SequenceEqual(new[] { ("down", .8, .7), ("up", .8, .7) }),
                "Key release did not release the transformed target exactly once.");
            holds.Complete(mapping.Id, release);
        }

        events.Clear();
        var earlyRelease = holds.Begin(entry);
        holds.Process(entry.Key!, false);
        await Cancelled(executor.ExecuteAsync(entry, route, earlyRelease.Token));
        MappingAssert(events.Count == 0, "Release before dispatch left a late contact.");
        holds.Complete(entry.Id, earlyRelease);

        // An unrelated key's release must leave this hold active. Focus/session
        // invalidation and global cancellation must both clean up its contact.
        foreach (var invalidateRoute in new[] { true, false })
        {
            events.Clear(); current = true;
            var release = holds.Begin(entry);
            var task = executor.ExecuteAsync(entry, route, release.Token);
            holds.Process(new MappedKey(0x4B, 0x25, false), false);
            MappingAssert(!release.IsCancellationRequested, "Unrelated key released the hold.");
            if (invalidateRoute) current = false; else holds.Cancel();
            await Cancelled(task);
            MappingAssert(events.Select(e => e.Action).SequenceEqual(new[] { "down", "up" }),
                "Context cancellation left a contact held.");
            holds.Complete(entry.Id, release);
        }
        current = true;

        // Independent holds release individually; modifier chords retire the
        // modifier contact as soon as another key is pressed.
        var modifier = entry with { Id = Guid.NewGuid(), Key = new(0xA2, 0x1D, false) };
        var modifierRelease = holds.Begin(modifier);
        var ordinaryRelease = holds.Begin(entry);
        var modifierTask = executor.ExecuteAsync(modifier, route, modifierRelease.Token);
        var ordinaryTask = executor.ExecuteAsync(entry, route, ordinaryRelease.Token);
        holds.Process(entry.Key!, true);
        await Cancelled(modifierTask);
        MappingAssert(!ordinaryTask.IsCompleted, "Modifier chord released an independent ordinary hold.");
        holds.Process(entry.Key!, false);
        await Cancelled(ordinaryTask);
        holds.Complete(modifier.Id, modifierRelease);
        holds.Complete(entry.Id, ordinaryRelease);

        // Repress during an asynchronous up waits for that up, then starts.
        events.Clear();
        var upStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRoute = route with { SendAsync = async (action, x, y, _) =>
        {
            events.Add((action, x, y));
            if (action == "up") { upStarted.TrySetResult(); await finishUp.Task; }
        } };
        var firstRelease = holds.Begin(entry);
        var first = executor.ExecuteAsync(entry, slowRoute, firstRelease.Token);
        holds.Process(entry.Key!, false);
        await upStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var nextRelease = holds.Begin(entry);
        var next = executor.ExecuteAsync(entry, route, nextRelease.Token);
        MappingAssert(events.Count == 2 && !next.IsCompleted, "Repress overlapped or was lost during old release.");
        finishUp.SetResult();
        await Cancelled(first);
        holds.Complete(entry.Id, firstRelease);
        await Task.Delay(40);
        MappingAssert(events.Select(e => e.Action).SequenceEqual(new[] { "down", "up", "down" }),
            "Repress failed to start after the old up.");
        holds.Process(entry.Key!, false);
        await Cancelled(next);
        holds.Complete(entry.Id, nextRelease);
        MappingAssert(!executor.IsBusy && events.Count == 4, "Repress cleanup leaked an active hold.");
        Console.WriteLine("PASS hold until release: physical down/up, modifiers/Win, repeat, early release, independent contacts, focus/cancel and rapid repress ordering.");
    }
}
