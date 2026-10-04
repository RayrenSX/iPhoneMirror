using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Services;

internal static class ClipboardTextReader
{
    // Called on the dispatcher; keep its STA context across busy retries.
    internal static async Task<string?> ReadAsync(Func<string> readText, Func<bool> canRead,
        Func<Task>? retryDelay = null)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (!canRead()) return null;
            try { return readText(); }
            catch (ExternalException) when (attempt < 5)
            {
                await (retryDelay?.Invoke() ?? Task.Delay(100 * attempt));
            }
        }
        return null;
    }
}
