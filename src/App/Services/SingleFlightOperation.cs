namespace IPhoneMirror.App.Services;

// Shares one asynchronous operation across duplicate requests. Cancellation
// waits for its cleanup, so callers can safely dispose resources afterwards.
internal sealed class SingleFlightOperation
{
    private readonly object _gate = new();
    private Task _task = Task.CompletedTask;
    private CancellationTokenSource? _cancellation;

    internal Task RunAsync(Func<CancellationToken, Task> operation,
        CancellationToken shutdown = default)
    {
        lock (_gate)
        {
            if (!_task.IsCompleted) return _task;
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            _cancellation = cancellation;
            _task = completion.Task;
            _ = ExecuteAsync(operation, cancellation, completion);
            return completion.Task;
        }
    }

    internal Task CancelAsync()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            return _task;
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (_gate)
            {
                _cancellation = null;
                cancellation.Dispose();
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }
    }
}
