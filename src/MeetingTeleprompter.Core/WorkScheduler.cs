namespace MeetingTeleprompter.Core;

// Audio and ASR own independent loops; this queue is for bounded, lower-priority work.
public sealed class WorkScheduler : IAsyncDisposable
{
    private readonly PriorityQueue<Func<CancellationToken, Task>, (int, long)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly object _gate = new();
    private long _sequence;
    public event Action<Exception>? Failed;
    public WorkScheduler() => _worker = Task.Run(ProcessAsync);
    public bool Enqueue(WorkPriority priority, Func<CancellationToken, Task> work)
    {
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested || _queue.Count >= 32) return false;
            _queue.Enqueue(work, ((int)priority, _sequence++));
            _signal.Release();
            return true;
        }
    }
    private async Task ProcessAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_shutdown.Token); } catch (OperationCanceledException) { break; }
            Func<CancellationToken, Task>? work;
            lock (_gate) work = _queue.Count > 0 ? _queue.Dequeue() : null;
            if (work is null) continue;
            try { await work(_shutdown.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Failed?.Invoke(error); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _worker;
        lock (_gate) _queue.Clear();
        _signal.Dispose();
        _shutdown.Dispose();
    }
}
