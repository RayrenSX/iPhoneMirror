namespace IPhoneMirror.App.Services;

/// <summary>
/// Makes the window-close cleanup ordered and idempotent. Core disposal is
/// attempted even if the explicit protocol stop reports an error.
/// </summary>
internal sealed class CaptureShutdownCoordinator
{
    private int _started;

    internal async Task StopAndDisposeOnceAsync(
        Func<Task> stopCapture,
        Func<Task> disposeCore)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        Exception? stopError = null;
        try
        {
            await stopCapture();
        }
        catch (Exception error)
        {
            stopError = error;
        }

        try
        {
            await disposeCore();
        }
        catch (Exception disposeError) when (stopError is not null)
        {
            throw new AggregateException(stopError, disposeError);
        }

        if (stopError is not null) throw stopError;
    }
}
