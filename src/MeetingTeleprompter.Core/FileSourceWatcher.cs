namespace MeetingTeleprompter.Core;

public sealed class FileSourceWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<string, CancellationToken, Task> _index;
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;
    public event Action<string>? Failed;
    public FileSourceWatcher(string folder, Func<string, CancellationToken, Task> index, TimeSpan? debounce = null)
    {
        _index = index;
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Created += (_, e) => Schedule(e.FullPath);
        _watcher.Changed += (_, e) => Schedule(e.FullPath);
        _watcher.Deleted += (_, e) => Schedule(e.FullPath);
        _watcher.Renamed += (_, e) => { Schedule(e.OldFullPath); Schedule(e.FullPath); };
        _watcher.Error += (_, _) => Failed?.Invoke("Folder watcher overflowed; refresh sources to reconcile changes.");
        _watcher.EnableRaisingEvents = true;
    }
    private void Schedule(string path)
    {
        if (!DocumentIngestor.Supports(path)) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending.TryGetValue(path, out var previous)) previous.Cancel();
            var cts = new CancellationTokenSource();
            _pending[path] = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000, cts.Token);
                    for (var attempt = 0; ; attempt++)
                    {
                        try { await _index(path, cts.Token); break; }
                        catch (IOException) when (attempt < 3) { await Task.Delay(750, cts.Token); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Failed?.Invoke(ex.Message); }
                finally
                {
                    lock (_gate) { if (_pending.TryGetValue(path, out var current) && current == cts) _pending.Remove(path); }
                    cts.Dispose();
                }
            });
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; _watcher.Dispose();
            foreach (var pending in _pending.Values) pending.Cancel();
        }
    }
}
