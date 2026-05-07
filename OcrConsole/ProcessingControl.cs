namespace OcrConsole;

internal sealed class ProcessingControl
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _resumeSignal = CreateSignal(completed: true);

    public bool IsPaused { get; private set; }

    public void Pause()
    {
        lock (_sync)
        {
            if (IsPaused) return;
            IsPaused = true;
            _resumeSignal = CreateSignal(completed: false);
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!IsPaused) return;
            IsPaused = false;
            _resumeSignal.TrySetResult(true);
        }
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_sync)
        {
            waitTask = _resumeSignal.Task;
        }

        if (waitTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        return waitTask.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<bool> CreateSignal(bool completed)
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            signal.TrySetResult(true);
        }

        return signal;
    }
}
