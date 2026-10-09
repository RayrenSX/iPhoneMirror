using System.Diagnostics;
using System.IO;
using System.Reflection;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunOwnedRuntimeCheck(string executable)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
        };
        info.ArgumentList.Add("--check-runtime");
        using var owner = OwnedBridgeProcess.Start(info);
        var output = owner.Output.ReadToEndAsync();
        var error = owner.Error.ReadToEndAsync();
        owner.Input.Close();
        owner.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        owner.RetireAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var result = output.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        var diagnostic = error.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        using var payload = System.Text.Json.JsonDocument.Parse(result.Trim());
        if (owner.Process.ExitCode != 0 || payload.RootElement.GetProperty("runtime").GetString() != "ready" ||
            owner.ActiveProcesses != 0 || owner.WasForced)
            throw new InvalidOperationException("Published bridge failed its owned startup/exit runtime check. " + diagnostic);
        Console.WriteLine("Published bridge functionality, redirected pipes, graceful exit and empty owned job passed; no phone connected.");
        return 0;
    }

    private static int RunBridgeProcessLifecycleTests(string python)
    {
        RunBridgeProcessLifecycleAsync(python).GetAwaiter().GetResult();
        Console.WriteLine("Bridge process start/stop/restart, cancellation and stale-exit isolation passed.");
        return 0;
    }

    private static async Task RunBridgeProcessLifecycleAsync(string python)
    {
        DiagnosticLogger.Initialize();
        var directory = Path.Combine(Path.GetTempPath(), "iphoneMirror-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "bridge.py");
        await File.WriteAllTextAsync(script, """
            import json, pathlib, subprocess, sys, time
            udid = sys.argv[sys.argv.index('--udid') + 1]
            if udid == 'slow-device': time.sleep(.5)
            if udid in ('stubborn-device', 'orphan-device', 'crash-device'):
                child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'],
                                         stdin=subprocess.DEVNULL)
                pathlib.Path(__file__).with_name('child.pid').write_text(str(child.pid))
            print(json.dumps({'event':'ready', 'protocol':2, 'udid':udid, 'transport':'usb',
                              'rateHz':120, 'gateOpen':True, 'authMode':'direct', 'generation':1}), flush=True)
            if udid == 'stubborn-device': time.sleep(60)
            if udid == 'orphan-device': sys.exit(0)
            sys.stdin.buffer.read()
            """);
        await using var bridge = new DirectUsbInputBridge();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await bridge.StartAsync(python, script, "test-device", ct: deadline.Token);
                if (!bridge.IsReady) throw new InvalidOperationException("Restart did not reach ready.");
                // Simulate an old Process.Exited callback arriving after a new
                // bridge is ready. It must not change the new session's gate.
                using var oldProcess = new Process();
                var callback = typeof(DirectUsbInputBridge).GetMethod("HandleProcessExitAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)callback.Invoke(bridge, [oldProcess])!;
                if (!bridge.IsReady || bridge.LastErrorCode is not null)
                    throw new InvalidOperationException("A stale process exit damaged the new connection.");
                await bridge.StopAsync();
                if (bridge.IsReady || bridge.GateOpen)
                    throw new InvalidOperationException("Stop left the input gate open.");
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await bridge.StartAsync(python, script, "test-device", ct: cancelled.Token);
                throw new InvalidOperationException("Cancelled startup launched a process.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }

            // Stop two callers while ready is still pending, then start again
            // before their cleanup has finished. No late reader may close the
            // new gate, and the cancelled startup must not wait 360 seconds.
            var pending = bridge.StartAsync(python, script, "slow-device");
            var firstStop = bridge.StopAsync();
            var secondStop = bridge.StopAsync();
            var restarted = bridge.StartAsync(python, script, "test-device");
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("Stopped startup completed successfully.");
            }
            catch (OperationCanceledException) { }
            await Task.WhenAll(firstStop, secondStop, restarted).WaitAsync(TimeSpan.FromSeconds(8));
            if (!bridge.IsReady) throw new InvalidOperationException("Concurrent stop/start lost readiness.");

            var oldGeneration = bridge.InputGeneration;
            await bridge.StopAsync();
            await bridge.StartAsync(python, script, "test-device");
            if (bridge.InputGeneration == oldGeneration)
                throw new InvalidOperationException("A restarted child reused the old host input lifetime.");
            try
            {
                await bridge.SendTouchBatchAsync([new TouchPoint(1, "down", .5, .5)], 0, 1,
                    expectedGeneration: oldGeneration);
                throw new InvalidOperationException("A pre-restart gesture entered the new child.");
            }
            catch (OperationCanceledException) { }

            // Capture the new packet: Python must still see its own generation
            // 1, even though the host has advanced several lifetimes.
            var stdinField = typeof(DirectUsbInputBridge).GetField("_stdin", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var actualStdin = stdinField.GetValue(bridge);
            using (var packets = new MemoryStream())
            using (var packetWriter = new StreamWriter(packets, leaveOpen: true))
            {
                stdinField.SetValue(bridge, packetWriter);
                try
                {
                    await bridge.SendKeyboardAsync([4]);
                    using var packet = System.Text.Json.JsonDocument.Parse(packets.ToArray().AsMemory(4));
                    if (packet.RootElement.GetProperty("generation").GetInt64() != 1)
                        throw new InvalidOperationException("The host lifetime leaked into the bridge wire protocol.");
                }
                finally { stdinField.SetValue(bridge, actualStdin); }
            }

            using (var oldProcess = new Process())
            using (var oldOutput = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                       "{\"event\":\"error\",\"code\":\"obsolete\"}\n"))))
            {
                var reader = typeof(DirectUsbInputBridge).GetMethod("ReadLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                await (Task)reader.Invoke(bridge, [oldProcess, oldOutput, CancellationToken.None])!;
                if (!bridge.IsReady || bridge.LastErrorCode is not null)
                    throw new InvalidOperationException("An old stdout reader damaged the new process.");
            }
            await bridge.StopAsync();

            // Hold the inner launch lock so the host can be stopped before
            // process creation. The stop token must prevent a delayed launch.
            await using var host = new UsbTouchBridgeHost();
            var inner = (DirectUsbInputBridge)typeof(UsbTouchBridgeHost).GetField("_bridge",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var lifecycle = (SemaphoreSlim)typeof(DirectUsbInputBridge).GetField("_lifecycleLock",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inner)!;
            await lifecycle.WaitAsync();
            var hostStart = host.StartAsync(UsbTouchTransport.Wireless, "test-device", "unused.exe");
            var hostStop = host.StopAsync();
            lifecycle.Release();
            try
            {
                await hostStart.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("A stopped host launched a process.");
            }
            catch (OperationCanceledException) { }
            await hostStop.WaitAsync(TimeSpan.FromSeconds(5));
            if (host.IsReady || host.State != ReverseControlState.Idle)
                throw new InvalidOperationException("Stopped host retained a ready state.");

            // A frozen bridge can have an interpreter child that still owns
            // USB handles and redirected output after the launcher is killed.
            // Force the real graceful-stop timeout with our own device-free
            // process tree and prove that Stop retires both processes.
            await bridge.StartAsync(python, script, "stubborn-device");
            var childMarker = Path.Combine(directory, "child.pid");
            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childMarker)));
            var ownedLauncher = (Process)typeof(DirectUsbInputBridge).GetField("_process",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
            Console.WriteLine($"Forced stop fixture: launcher={ownedLauncher.Id}, child={child.Id}");
            try
            {
                Console.WriteLine($"Before stop: launcher_exited={ownedLauncher.HasExited}, child_exited={child.HasExited}, app={typeof(DirectUsbInputBridge).Assembly.Location}");
                var forcedStopTimer = Stopwatch.StartNew();
                await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine($"After stop: seconds={forcedStopTimer.Elapsed.TotalSeconds:F2}, child_exited={child.HasExited}");
                if (!child.HasExited)
                    throw new InvalidOperationException("Forced bridge stop left its interpreter child alive.");
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                File.Delete(childMarker);
            }

            // Killing only the launcher must still publish a terminal event
            // and retire descendants, even when they retain the output pipe.
            await bridge.StartAsync(python, script, "crash-device");
            using var crashChild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childMarker)));
            var crashLauncher = (Process)typeof(DirectUsbInputBridge).GetField("_process",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
            try
            {
                crashLauncher.Kill();
                var crashTimer = Stopwatch.StartNew();
                while (bridge.IsReady && crashTimer.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(20);
                if (bridge.IsReady) throw new InvalidOperationException("Launcher crash never closed the ready gate.");
                await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                if (!crashChild.HasExited)
                    throw new InvalidOperationException("Launcher crash left an owned child alive.");
            }
            finally
            {
                if (!crashChild.HasExited) crashChild.Kill(entireProcessTree: true);
                File.Delete(childMarker);
            }

            // The kernel job also catches children after a launcher exits
            // normally, and never takes ownership of an unrelated process.
            var independentInfo = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
            independentInfo.ArgumentList.Add("-c");
            independentInfo.ArgumentList.Add("import time; time.sleep(60)");
            using var independent = Process.Start(independentInfo)!;
            var orphanInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            orphanInfo.ArgumentList.Add(script);
            orphanInfo.ArgumentList.Add("--udid");
            orphanInfo.ArgumentList.Add("orphan-device");
            using var orphan = OwnedBridgeProcess.Start(orphanInfo);
            try
            {
                await orphan.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var orphanChild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childMarker)));
                await orphan.RetireAsync(TimeSpan.Zero);
                if (!orphanChild.HasExited || independent.HasExited || orphan.ActiveProcesses != 0)
                    throw new InvalidOperationException("Job retirement did not isolate the orphaned bridge lifetime.");
            }
            finally
            {
                if (!independent.HasExited) independent.Kill(entireProcessTree: true);
                await independent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                File.Delete(childMarker);
            }

            var unusualArgument = "中文 path\\\"quoted value\\";
            var unicodeInfo = new ProcessStartInfo(python)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            unicodeInfo.ArgumentList.Add("-c");
            unicodeInfo.ArgumentList.Add("import json, os, sys; print(json.dumps([sys.argv[1:], os.environ['BRIDGE_LAUNCH_TEST']], ensure_ascii=True), flush=True); print('diagnostic', file=sys.stderr, flush=True); sys.stdin.buffer.read()");
            unicodeInfo.ArgumentList.Add(unusualArgument);
            unicodeInfo.ArgumentList.Add(string.Empty);
            unicodeInfo.Environment["BRIDGE_LAUNCH_TEST"] = "中文环境\\value";
            using (var unicode = OwnedBridgeProcess.Start(unicodeInfo))
            {
                var line = await unicode.Output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var payload = System.Text.Json.JsonDocument.Parse(line!);
                var values = payload.RootElement;
                if (values[0][0].GetString() != unusualArgument || values[0][1].GetString() != string.Empty ||
                    values[1].GetString() != unicodeInfo.Environment["BRIDGE_LAUNCH_TEST"] ||
                    await unicode.Error.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "diagnostic")
                    throw new InvalidOperationException("Owned launch changed Unicode/quoted arguments, environment, or stderr.");
                unicode.Input.Close();
                await unicode.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await unicode.RetireAsync(TimeSpan.FromSeconds(1));
                if (unicode.ActiveProcesses != 0 || unicode.WasForced)
                    throw new InvalidOperationException("Normal owned launch did not retire gracefully.");
            }

            var missing = new ProcessStartInfo(Path.Combine(directory, "missing.exe"))
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            using var currentProcess = Process.GetCurrentProcess();
            var beforeFailureHandles = currentProcess.HandleCount;
            var steadyFailureHandles = beforeFailureHandles;
            for (var attempt = 0; attempt < 12; ++attempt)
            {
                try
                {
                    using var failed = OwnedBridgeProcess.Start(missing);
                    throw new InvalidOperationException("Missing bridge executable was launched.");
                }
                catch (System.ComponentModel.Win32Exception) { }
                currentProcess.Refresh();
                // The first Win32Exception initializes error/culture support
                // (the trace showed +5 once and no growth over later calls).
                // Check repeated failures against the first failure, keeping
                // the cold-start counts visible instead of calling it a leak.
                if (attempt == 0) steadyFailureHandles = currentProcess.HandleCount;
                Console.WriteLine($"Failed launch {attempt}: handles={currentProcess.HandleCount}, baseline={beforeFailureHandles}");
            }
            currentProcess.Refresh();
            if (currentProcess.HandleCount > steadyFailureHandles + 2)
                throw new InvalidOperationException("Failed owned launches leaked pipe/job handles.");
        }
        finally
        {
            await bridge.StopAsync();
            File.Delete(script);
            Directory.Delete(directory);
        }
    }
}
