using System.Diagnostics;
using System.IO;
using System.Text.Json;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Opt-in because this verifies the real host deadline, including a valid
    // reply after the old 15-second limit and cleanup of a silent request.
    private static int RunClipboardTimeoutTests()
    {
        RunClipboardTimeoutTestsAsync().GetAwaiter().GetResult();
        Console.WriteLine("Clipboard deadlines: delayed valid reply, silent-request cancellation acknowledgement and next-request recovery passed.");
        return 0;
    }

    private static async Task RunClipboardTimeoutTestsAsync()
    {
        using var bytes = new MemoryStream();
        using var writer = new StreamWriter(bytes, leaveOpen: true);
        await using var bridge = new DirectUsbInputBridge();
        SetKeyboardField(bridge, "_stdin", writer);
        SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        SetKeyboardField(bridge, "<SupportsAutomationClipboard>k__BackingField", true);
        JsonElement Frame(int offset)
        {
            var packet = bytes.ToArray();
            var size = BitConverter.ToInt32(packet, offset);
            return JsonDocument.Parse(packet.AsMemory(offset + 4, size)).RootElement.Clone();
        }
        void Reply(JsonElement request, bool success, string? text = null)
        {
            var response = JsonSerializer.SerializeToElement(new
            {
                requestId = request.GetProperty("requestId").GetString(),
                generation = request.GetProperty("generation").GetInt64(), success, text
            });
            KeyboardCall(bridge, "CompleteAutomationClipboard", response);
        }

        var delayed = bridge.AutomationClipboardAsync("read_clipboard", null, () => true, default);
        var first = Frame(0);
        await Task.Delay(TimeSpan.FromSeconds(16));
        if (delayed.IsCompleted)
            throw new InvalidOperationException("A valid clipboard request was cancelled at the obsolete 15-second deadline.");
        Reply(first, true, "delayed complete text");
        if (await delayed != "delayed complete text")
            throw new InvalidOperationException("A valid delayed reply was lost.");

        var offset = (int)bytes.Length;
        var silent = bridge.AutomationClipboardAsync("read_clipboard", null, () => true, default);
        var second = Frame(offset);
        offset = (int)bytes.Length;
        var clock = Stopwatch.StartNew();
        while (bytes.Length == offset && clock.Elapsed < TimeSpan.FromSeconds(40))
            await Task.Delay(10);
        if (bytes.Length == offset || Frame(offset).GetProperty("kind").GetString() != "cancel_clipboard")
            throw new InvalidOperationException("A silent clipboard request was left running without cancellation.");
        Reply(second, false);
        try
        {
            await silent;
            throw new InvalidOperationException("A silent request reported success.");
        }
        catch (TimeoutException) { }
        if (!bridge.IsReady)
            throw new InvalidOperationException("Acknowledged timeout cleanup stopped healthy device control.");

        offset = (int)bytes.Length;
        using var cancellation = new CancellationTokenSource();
        var cancelling = bridge.AutomationClipboardAsync("paste_text", "cancelled value", () => true, cancellation.Token);
        var third = Frame(offset);
        cancellation.Cancel();
        // The bridge can own three diagnostic channels with one-second
        // cleanup bounds. Its acknowledgement must outlive the old 2s limit.
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (cancelling.IsCompleted || !bridge.IsReady)
            throw new InvalidOperationException("The host retired healthy control before diagnostic cleanup could acknowledge cancellation.");
        Reply(third, false);
        try
        {
            await cancelling;
            throw new InvalidOperationException("Cancelled input reported success.");
        }
        catch (OperationCanceledException) { }

        offset = (int)bytes.Length;
        var next = bridge.AutomationClipboardAsync("write_clipboard", "next value", () => true, default);
        Reply(Frame(offset), true);
        await next;
    }
}
